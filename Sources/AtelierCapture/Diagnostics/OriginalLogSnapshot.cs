using System.Globalization;
using System.Security.Cryptography;

namespace AtelierCapture;

internal static class OriginalLogSnapshot
{
    internal static void Capture(string path, string source, Action<string, object> emit)
    {
        var snapshot = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        long copied = 0;
        try
        {
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            var length = input.Length;
            emit(
                "startup_log_original",
                new
                {
                    snapshot_id = snapshot,
                    source,
                    path,
                    phase = "prefix_begin",
                    available_prefix_bytes = length,
                    utc = started,
                    origin = "process_log_file_unclassified",
                    coverage = "exact_available_prefix_only_unflushed_console_and_earlier_rotated_files_not_proven",
                }
            );
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            while (copied < length)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - copied));
                if (read == 0)
                    throw new EndOfStreamException(
                        "Original log prefix changed while being preserved."
                    );
                var original = buffer.AsSpan(0, read).ToArray();
                hash.AppendData(original);
                emit(
                    "startup_log_bytes",
                    new
                    {
                        snapshot_id = snapshot,
                        source,
                        offset = copied.ToString(CultureInfo.InvariantCulture),
                        bytes = original,
                    }
                );
                copied += read;
            }
            emit(
                "startup_log_original",
                new
                {
                    snapshot_id = snapshot,
                    source,
                    path,
                    phase = "prefix_complete",
                    original_bytes = copied,
                    sha256 = Convert.ToHexString(hash.GetHashAndReset()),
                    utc = DateTimeOffset.UtcNow,
                    started,
                    origin = "process_log_file_unclassified",
                    coverage = "exact_available_prefix_only_unflushed_console_and_earlier_rotated_files_not_proven",
                }
            );
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException
            )
        {
            emit(
                "startup_log_original",
                new
                {
                    snapshot_id = snapshot,
                    source,
                    path,
                    phase = copied == 0 ? "unavailable" : "partial_read",
                    preserved_bytes = copied,
                    error = error.GetType().FullName,
                    stack = error.ToString(),
                    utc = DateTimeOffset.UtcNow,
                    origin = "process_log_file_unclassified",
                    completeness = "original_log_read_failed",
                }
            );
        }
    }
}
