using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AtelierCapture.Update;

internal sealed class UpdateTransaction
{
    private readonly UpdateFiles files;
    private readonly Action<string>? checkpoint;

    internal UpdateTransaction(UpdateFiles files, Action<string>? checkpoint = null)
    {
        this.files = files;
        this.checkpoint = checkpoint;
    }

    internal IDisposable Enter(int timeoutMilliseconds = 30000)
    {
        files.Initialize();
        var identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(files.Root.ToUpperInvariant()))
        );
        var mutex = new Mutex(false, "Local\\AtelierCapture.Update." + identity);
        try
        {
            try
            {
                if (!mutex.WaitOne(timeoutMilliseconds))
                    throw new TimeoutException("Capture update installation is still busy.");
            }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    internal FileStream GameLease(bool exclusive)
    {
        var path = files.Resolve("BepInEx/capture-update/game.lock");
        if (!File.Exists(path))
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        return new FileStream(
            path,
            FileMode.Open,
            exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.None : FileShare.Read
        );
    }

    internal void Recover()
    {
        if (!File.Exists(files.Journal))
            return;
        var journal = files.Read<UpdateJournal>(files.Journal);
        ValidateJournal(journal);
        // Replacement is atomic per file. A third identity means another installer/user intervened.
        foreach (var before in journal.Before)
        {
            var target = files.Resolve(before.Path);
            var after = journal.After.Single(s => s.Path == before.Path);
            if (!UpdateFiles.Matches(target, before) && !UpdateFiles.Matches(target, after))
                throw new InvalidDataException(
                    "Unrecognized recorder file during recovery; originals retained."
                );
            if (!UpdateFiles.Matches(Backup(before.Path), before))
                throw new InvalidDataException(
                    "Update rollback copy is unavailable; capture remains blocked."
                );
        }
        if (journal.After.All(s => UpdateFiles.Matches(files.Resolve(s.Path), s)))
        {
            Finish("Update installed; previous recorder files retained for recovery.");
            return;
        }
        RollBack(journal);
    }

    internal bool ApplyReady()
    {
        if (File.Exists(files.Journal))
            throw new InvalidOperationException("Recover the previous update first.");
        if (!File.Exists(files.Ready) || !files.Enabled())
            return false;
        using var package = new UpdatePackage(files.Ready);
        using var current = System.Text.Json.JsonDocument.Parse(
            UpdateFiles.ReadSmall(files.Resolve(UpdateFiles.Certificate))
        );
        var installed = UpdateFiles.Version(
            current.RootElement.GetProperty("capture_version").GetString()!
        );
        if (UpdateFiles.Version(package.Manifest.Version) <= installed)
        {
            package.Dispose();
            File.Delete(files.Ready);
            return false;
        }
        package.CheckInstalled(files);
        var before = UpdateFiles
            .Targets.Select(p => UpdateFiles.Stamp(p, files.Resolve(p)))
            .ToArray();
        var needed =
            before.Sum(s => s.Size)
            + package.Manifest.Files.Sum(s => s.Size)
            + package.Manifest.Files.Max(s => s.Size)
            + 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(files.State)!).AvailableFreeSpace < needed)
            throw new IOException("Insufficient space for capture update and rollback copies.");
        var payload = files.Resolve("BepInEx/capture-update/payload");
        var backup = files.Resolve("BepInEx/capture-update/backup");
        Directory.CreateDirectory(backup);
        package.Extract(files, payload);
        foreach (var stamp in before)
        {
            files.CopyDurable(files.Resolve(stamp.Path), Backup(stamp.Path));
            if (!UpdateFiles.Matches(Backup(stamp.Path), stamp))
                throw new IOException("Update backup differs.");
        }
        var journal = new UpdateJournal(1, before, package.Manifest.Files);
        files.Write(files.Journal, journal);
        checkpoint?.Invoke("journal");
        try
        {
            for (var index = 0; index < journal.After.Length; index++)
            {
                var stamp = journal.After[index];
                if (!UpdateFiles.Matches(files.Resolve(stamp.Path), before[index]))
                    throw new IOException("Installed recorder changed during update.");
                checkpoint?.Invoke("before_replace_" + index);
                files.Replace(
                    Path.Combine(payload, Path.GetFileName(stamp.Path)),
                    files.Resolve(stamp.Path)
                );
                checkpoint?.Invoke("after_replace_" + index);
            }
            if (!journal.After.All(s => UpdateFiles.Matches(files.Resolve(s.Path), s)))
                throw new IOException("Installed capture update verification failed.");
            checkpoint?.Invoke("verified");
            package.Dispose();
            Finish("Update installed; previous recorder files retained for recovery.");
            checkpoint?.Invoke("finished");
            return true;
        }
        catch
        {
            package.Dispose();
            // Use the same identity checks as a cold restart; never overwrite unfamiliar files.
            Recover();
            throw;
        }
    }

    internal bool TryApplyReady(Action<string> report)
    {
        try
        {
            return ApplyReady();
        }
        catch (Exception error)
        {
            report("Capture update deferred: " + error.Message);
            if (File.Exists(files.Journal))
                throw;
            if (File.Exists(files.Ready))
                File.Move(files.Ready, files.Resolve("BepInEx/capture-update/failed.zip"), true);
            files.Write(
                files.Resolve("BepInEx/capture-update/status.json"),
                new
                {
                    status = "Update deferred; later release checks remain enabled.",
                    error = error.Message,
                }
            );
            return false;
        }
    }

    private void RollBack(UpdateJournal journal)
    {
        for (var index = 0; index < journal.Before.Length; index++)
        {
            var stamp = journal.Before[index];
            if (!UpdateFiles.Matches(files.Resolve(stamp.Path), stamp))
                files.Replace(Backup(stamp.Path), files.Resolve(stamp.Path));
            checkpoint?.Invoke("rollback_" + index);
        }
        if (!journal.Before.All(s => UpdateFiles.Matches(files.Resolve(s.Path), s)))
            throw new IOException("Capture update rollback verification failed.");
        if (File.Exists(files.Ready))
            File.Move(files.Ready, files.Resolve("BepInEx/capture-update/failed.zip"), true);
        files.Write(
            files.Resolve("BepInEx/capture-update/status.json"),
            new { status = "Update interrupted; previous recorder restored." }
        );
        File.Delete(files.Journal);
    }

    private void Finish(string status)
    {
        files.Write(files.Resolve("BepInEx/capture-update/status.json"), new { status });
        File.Delete(files.Journal);
        if (File.Exists(files.Ready))
            File.Delete(files.Ready);
        foreach (var stamp in UpdateFiles.Targets)
        {
            var payload = files.Resolve(
                "BepInEx/capture-update/payload/" + Path.GetFileName(stamp)
            );
            if (File.Exists(payload))
                File.Delete(payload);
            var temporary = files.Resolve(stamp) + ".capture-update";
            files.Ordinary(temporary);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private string Backup(string path) =>
        files.Resolve("BepInEx/capture-update/backup/" + Path.GetFileName(path));

    private static void ValidateJournal(UpdateJournal journal)
    {
        if (
            journal.Schema != 1
            || journal.Before is null
            || journal.After is null
            || !journal.Before.Select(s => s.Path).SequenceEqual(UpdateFiles.Targets)
            || !journal.After.Select(s => s.Path).SequenceEqual(UpdateFiles.Targets)
        )
            throw new InvalidDataException("Unrecognized capture update transaction.");
        foreach (var stamp in journal.Before.Concat(journal.After))
            UpdateFiles.ValidateStamp(stamp);
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
