using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AtelierCapture.Archive;
using AtelierCapture.Storage;
using Microsoft.Win32.SafeHandles;

namespace AtelierCapture;

internal sealed partial class SessionWriter
{
    private void CleanupCurrentWorkingFiles(VerifiedCapture?[] packages)
    {
        var current = sources.Where(source => source.Folder is not null).ToArray();
        string? reason = null;
        if (aborted.IsCancellationRequested)
            reason = "explicit_abort";
        else if (!sources.Take(2).Any(source => source.Initialized))
            reason = "process_has_no_known_environment_archive";
        else
        {
            for (var i = 0; i < 2; i++)
            {
                var source = sources[i];
                if (!source.Initialized)
                    continue;
                var package = packages[i];
                if (package is null || source.ArchivePending)
                {
                    reason = "environment_archive_pending";
                    break;
                }
                if (
                    !source.NormalClosed
                    || !package.NewlyCreated
                    || !package.NormalShutdown
                    || package.Recovered
                    || package.Aborted
                    || package.TailBytes != 0
                )
                {
                    reason = "archive_is_not_new_complete_physical_close";
                    break;
                }
                if (
                    package.RunId != RunId
                    || package.Environment != source.Name
                    || package.CheckpointGeneration != source.Generation
                    || package.CheckpointHash
                        != Convert.ToHexString(SHA256.HashData(source.CheckpointBytes!))
                    || !package.Streams.SequenceEqual(CurrentStamps(source))
                    || !string.Equals(
                        Path.GetFullPath(package.ArchivePath),
                        Path.ChangeExtension(source.Folder!, ".zip"),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    reason = "verified_archive_ownership_mismatch";
                    break;
                }
            }
            if (reason is null && sources[2].Initialized)
            {
                var process = sources[2];
                var targetIndex =
                    process.TransferEnvironment == "Official" ? 0
                    : process.TransferEnvironment == "Private" ? 1
                    : -1;
                var owner = targetIndex < 0 ? null : packages[targetIndex]?.BootstrapOwnership;
                if (
                    !process.Frozen
                    || !process.NormalClosed
                    || owner is null
                    || owner.RunId != RunId
                    || owner.Environment != "Unknown"
                    || owner.CheckpointHash
                        != Convert.ToHexString(SHA256.HashData(process.CheckpointBytes!))
                    || owner.DiagnosticsHash != process.Logs[3]!.Sha256
                    || owner.Records != process.Logs[3]!.Records
                    || owner.TransferredRecords != process.Logs[3]!.Records
                    || sources[targetIndex].BootstrapCopiedBytes != process.Logs[3]!.ExtractedBytes
                    || sources[targetIndex].BootstrapCopiedRecords != process.Logs[3]!.Records
                    || process.Logs.Take(3).Any(log => log!.Records != 0 || log.ExtractedBytes != 0)
                )
                    reason = "process_prefix_not_fully_owned_by_verified_archive";
                if (reason is null)
                    for (var i = 0; i < 2; i++)
                    {
                        if (i == targetIndex || !sources[i].Initialized)
                            continue;
                        var reference = packages[i]?.BootstrapOwnership;
                        if (
                            reference is null
                            || reference.RunId != owner!.RunId
                            || reference.Environment != owner.Environment
                            || reference.CheckpointHash != owner.CheckpointHash
                            || reference.DiagnosticsHash != owner.DiagnosticsHash
                            || reference.Records != owner.Records
                            || reference.TransferredRecords != 0
                            || reference.ReferenceRecords != 1
                        )
                            reason = "other_environment_process_reference_unverified";
                    }
            }
        }
        if (reason is not null)
        {
            foreach (var source in current)
            {
                source.RetentionReason = reason;
                SetRemainingFiles(source);
            }
            return;
        }
        var held = new List<(Source Owner, string Name, FileStream File)>();
        var directories = new List<SafeFileHandle>();
        try
        {
            foreach (var source in current)
            {
                var expected = Path.Combine(
                    root,
                    source.Name == "Unknown" ? "Process" : source.Name,
                    source.OwnedFolderName + ".partial"
                );
                if (
                    !string.Equals(source.Folder, expected, StringComparison.OrdinalIgnoreCase)
                    || new[] { root, Path.GetDirectoryName(expected)!, expected }.Any(path =>
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    )
                )
                    throw new InvalidDataException("working_path_is_not_owned_ordinary_directory");
                var names = StorageCheckpoint
                    .FileNames.Concat(
                        new[] { "session-state.0.json", "session-state.1.json", "session.lock" }
                    )
                    .ToHashSet(StringComparer.Ordinal);
                foreach (
                    var directory in new[] { root, Path.GetDirectoryName(expected)!, expected }
                )
                    directories.Add(OpenCleanupDirectory(directory));
                var files = Directory.GetFileSystemEntries(expected);
                if (
                    files.Length != names.Count
                    || files.Any(path =>
                        !names.Contains(Path.GetFileName(path))
                        || (
                            File.GetAttributes(path)
                            & (FileAttributes.Directory | FileAttributes.ReparsePoint)
                        ) != 0
                    )
                )
                    throw new InvalidDataException("unaccounted_working_file_or_directory");
                var lease = OpenCleanupFile(Path.Combine(expected, "session.lock"));
                held.Add((source, "session.lock", lease));
                if (lease.Length != 0)
                    throw new InvalidDataException("unaccounted_session_lock_bytes");
                foreach (var stamp in CurrentStamps(source))
                {
                    hooks?.Before?.Invoke(
                        StorageAction.CleanupOpen,
                        Path.Combine(expected, stamp.Name),
                        0
                    );
                    var file = OpenCleanupFile(Path.Combine(expected, stamp.Name));
                    held.Add((source, stamp.Name, file));
                    if (
                        file.Length != stamp.Bytes
                        || Convert.ToHexString(SHA256.HashData(file)) != stamp.Sha256
                    )
                        throw new InvalidDataException(
                            "source_has_unarchived_bytes_or_changed_identity"
                        );
                }
                var checkpoints = new List<(StorageCheckpointPayload Value, string Hash)>();
                foreach (var name in new[] { "session-state.0.json", "session-state.1.json" })
                {
                    var file = OpenCleanupFile(Path.Combine(expected, name));
                    held.Add((source, name, file));
                    if (file.Length <= 0 || file.Length > StorageCheckpoint.MaximumBytes)
                        throw new InvalidDataException("working_checkpoint_is_incomplete");
                    var bytes = new byte[(int)file.Length];
                    file.ReadExactly(bytes);
                    checkpoints.Add(
                        (
                            StorageCheckpoint.Decode(bytes),
                            Convert.ToHexString(SHA256.HashData(bytes))
                        )
                    );
                }
                var latest = checkpoints.MaxBy(value => value.Value.Generation);
                if (
                    checkpoints.Any(value =>
                        value.Value.RunId != RunId || value.Value.Environment != source.Name
                    )
                    || latest.Value.Generation != source.Generation
                    || latest.Hash != Convert.ToHexString(SHA256.HashData(source.CheckpointBytes!))
                )
                    throw new InvalidDataException("working_checkpoint_ownership_mismatch");
            }
            foreach (var item in held)
            {
                if (aborted.IsCancellationRequested)
                    throw new CaptureAbortedException("explicit_abort_during_cleanup");
                var itemPath = Path.Combine(item.Owner.Folder!, item.Name);
                hooks?.Before?.Invoke(
                    StorageAction.CleanupDelete,
                    itemPath,
                    item.Owner.RemovedFiles
                );
                if (aborted.IsCancellationRequested)
                    throw new CaptureAbortedException("explicit_abort_during_cleanup");
                item.Owner.CleanupStarted = true;
                var delete = true;
                if (!DeleteCleanupHandle(item.File.SafeFileHandle, 4, ref delete, 1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                item.Owner.RemovedFiles++;
                hooks?.After?.Invoke(
                    StorageAction.CleanupDelete,
                    itemPath,
                    item.Owner.RemovedFiles
                );
            }
            foreach (var item in held)
                item.File.Dispose();
            held.Clear();
            foreach (var directory in directories)
                directory.Dispose();
            directories.Clear();
            foreach (var source in current)
            {
                Directory.Delete(source.Folder!, recursive: false);
                source.WorkingFilesCleared = true;
                source.RetentionReason = null;
            }
        }
        catch (Exception ex)
        {
            reason =
                ex is InvalidDataException
                    ? ex.Message
                    : "working_cleanup_failed_" + ex.GetType().Name;
            foreach (var source in current.Where(source => !source.WorkingFilesCleared))
                source.RetentionReason = reason;
            Report($"Capture ZIPs remain verified; working cleanup is incomplete ({reason}).");
        }
        finally
        {
            foreach (var item in held)
                item.File.Dispose();
            foreach (var directory in directories)
                directory.Dispose();
            foreach (var source in current)
                SetRemainingFiles(source);
        }
    }

    private static void SetRemainingFiles(Source source)
    {
        try
        {
            source.RemainingFiles = Directory.Exists(source.Folder)
                ? Directory.GetFileSystemEntries(source.Folder!).Select(Path.GetFileName).ToArray()
                : [];
        }
        catch (Exception ex)
        {
            source.RetentionReason = "remaining_inventory_unavailable_" + ex.GetType().Name;
        }
    }

    private static FileStream OpenCleanupFile(string path)
    {
        var handle = OpenCleanupHandle(
            path,
            0x80010000,
            1,
            IntPtr.Zero,
            3,
            0x08200000,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            if (!InspectCleanupHandle(handle, out var facts))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (
                (facts.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory))
                != 0
            )
                throw new InvalidDataException("cleanup_handle_is_reparse_or_directory");
            CheckCleanupPath(handle, path);
            return new FileStream(handle, FileAccess.Read, 65536);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileStream OpenReservationFile(string path)
    {
        var handle = OpenCleanupHandle(path, 0x40010000, 0, IntPtr.Zero, 1, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Capture filename reservation failed.",
                new Win32Exception(error)
            );
        }
        try
        {
            return new FileStream(handle, FileAccess.Write, 4096);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenCleanupDirectory(string path)
    {
        var handle = OpenCleanupHandle(
            path,
            0x80000000,
            3,
            IntPtr.Zero,
            3,
            0x02200000,
            IntPtr.Zero
        );
        try
        {
            if (handle.IsInvalid || !InspectCleanupHandle(handle, out var facts))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (
                (facts.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                || (facts.Attributes & (uint)FileAttributes.Directory) == 0
            )
                throw new InvalidDataException("cleanup_parent_is_not_ordinary_directory");
            CheckCleanupPath(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void CheckCleanupPath(SafeFileHandle handle, string expected)
    {
        var text = new StringBuilder(32768);
        var size = ResolveCleanupHandle(handle, text, (uint)text.Capacity, 0);
        if (size == 0 || size >= text.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var actual = text.ToString();
        if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal))
            actual = actual[4..];
        if (
            !string.Equals(
                Path.GetFullPath(actual),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidDataException("cleanup_handle_resolved_path_mismatch");
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern uint ResolveCleanupHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint size,
        uint flags
    );

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafeFileHandle OpenCleanupHandle(
        string path,
        uint access,
        uint share,
        IntPtr security,
        uint creation,
        uint flags,
        IntPtr template
    );

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteCleanupHandle(
        SafeFileHandle file,
        int kind,
        [MarshalAs(UnmanagedType.U1)] ref bool delete,
        uint size
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct CleanupHandleFacts
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation,
            Access,
            Write;
        internal uint Volume,
            SizeHigh,
            SizeLow,
            Links,
            IndexHigh,
            IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InspectCleanupHandle(
        SafeFileHandle file,
        out CleanupHandleFacts facts
    );

    private static StorageStreamStamp[] CurrentStamps(Source source) =>
        source
            .Logs.Select(log => new StorageStreamStamp(
                Path.GetFileName(log!.Path),
                log.Bytes,
                log.Sha256,
                log.Records,
                log.ExtractedBytes
            ))
            .ToArray();
}
