using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AtelierCapture.Checks")]

namespace AtelierCapture.Update;

public static class UpdateHost
{
    private static FileStream? gameLease;

    public static void BeforePluginLoad(string root, bool enabled, Action<string> report)
    {
        if (gameLease is not null)
            throw new InvalidOperationException("Capture update startup already ran.");
        var files = new UpdateFiles(root);
        var transaction = new UpdateTransaction(files);
        using (transaction.Enter())
        {
            FileStream? exclusive = null;
            try
            {
                exclusive = transaction.GameLease(true);
            }
            catch (IOException) when (!File.Exists(files.Journal)) { }
            using (exclusive)
            {
                if (exclusive is not null)
                    transaction.Recover();
                if (exclusive is not null && enabled && files.Enabled())
                    transaction.TryApplyReady(report);
            }
            gameLease = transaction.GameLease(false);
            AppContext.SetData(UpdateFiles.GateKey, files.Root);
        }
        if (!enabled || !files.Enabled())
        {
            report("Capture automatic updates are disabled.");
            return;
        }
        try
        {
            using var current = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(files.Resolve("dotnet/AtelierCapture.Update.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = files.Root,
            };
            start.ArgumentList.Add("--watch");
            start.ArgumentList.Add(
                current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            start.ArgumentList.Add(
                current
                    .StartTime.ToUniversalTime()
                    .Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            using var helper =
                Process.Start(start)
                ?? throw new IOException("Could not start capture update helper.");
            report("Capture updater is checking stable releases in the background.");
        }
        catch (Exception error)
        {
            report("Capture update check unavailable; recording continues: " + error.Message);
        }
    }

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine("AtelierCapture.Update 1.0.0 / " + Environment.Version);
            return 0;
        }
        if (
            args.Length != 3
            || args[0] != "--watch"
            || !int.TryParse(args[1], out var processId)
            || !long.TryParse(args[2], out var ticks)
        )
            return 2;
        var files = new UpdateFiles(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!
        );
        try
        {
            files.Initialize();
            // Lifetime ownership is separate from the short install/startup gate.
            using var ownership = new FileStream(
                files.Resolve("BepInEx/capture-update/helper.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
            Process? parent = null;
            try
            {
                parent = Process.GetProcessById(processId);
                if (
                    parent.StartTime.ToUniversalTime().Ticks != ticks
                    || !string.Equals(
                        parent.MainModule?.FileName,
                        files.Resolve("AtelierResleriana.exe"),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    throw new InvalidDataException("Capture updater parent identity differs.");
            }
            catch (ArgumentException) { }
            using (parent)
            {
                try
                {
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (InvalidOperationException) { }
                await CheckForUpdate(files);
                if (!File.Exists(files.Ready))
                    return 0;
                if (parent is not null)
                    await parent.WaitForExitAsync();
            }
            var transaction = new UpdateTransaction(files);
            using (transaction.Enter())
            {
                FileStream exclusive;
                try
                {
                    exclusive = transaction.GameLease(true);
                }
                catch (IOException)
                {
                    return 0;
                }
                using (exclusive)
                {
                    transaction.Recover();
                    transaction.TryApplyReady(_ => { });
                }
            }
            return 0;
        }
        catch (IOException error)
            when (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
        {
            return 0;
        }
        catch (Exception error)
        {
            try
            {
                files.Write(
                    files.Resolve("BepInEx/capture-update/status.json"),
                    new { status = "Update deferred", error = error.Message }
                );
            }
            catch (Exception) { }
            return 1;
        }
    }

    private static async Task CheckForUpdate(UpdateFiles files)
    {
        if (!files.Enabled() || File.Exists(files.Ready))
            return;
        var checkedPath = files.Resolve("BepInEx/capture-update/last-check.json");
        if (File.Exists(checkedPath))
        {
            var last = files.Read<CheckTime>(checkedPath);
            var elapsed = DateTimeOffset.UtcNow - last.CheckedAt;
            if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromHours(1))
                return;
        }
        files.Write(checkedPath, new CheckTime(DateTimeOffset.UtcNow));
        using var document = JsonDocument.Parse(
            UpdateFiles.ReadSmall(files.Resolve(UpdateFiles.Certificate))
        );
        var current = UpdateFiles.Version(
            document.RootElement.GetProperty("capture_version").GetString()!
        );
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var download = new ReleaseDownload(client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var release = await download.Latest(current, deadline.Token);
        if (release is null)
            return;
        var path = files.Resolve("BepInEx/capture-update/download.partial");
        await download.Download(release, path, deadline.Token);
        using (var package = new UpdatePackage(path))
        {
            if (package.Manifest.Version != release.Version)
                throw new InvalidDataException("Update release/payload version differs.");
            package.CheckInstalled(files);
        }
        var transaction = new UpdateTransaction(files);
        // Named mutexes are thread-affine: never await while holding this lease.
        using (transaction.Enter())
        {
            if (!File.Exists(files.Ready) && !File.Exists(files.Journal))
                File.Move(path, files.Ready);
        }
    }

    private sealed record CheckTime(DateTimeOffset CheckedAt);
}
