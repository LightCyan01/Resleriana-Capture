using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace AtelierCapture.Update;

internal sealed class UpdatePackage : IDisposable
{
    private readonly FileStream file;
    private readonly ZipArchive zip;
    internal UpdateManifest Manifest { get; }
    internal JsonElement Certificate { get; }

    internal UpdatePackage(string path)
    {
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length is < 1 or > 32 * 1024 * 1024)
                throw new InvalidDataException("Update ZIP is too large.");
            zip = new ZipArchive(file, ZipArchiveMode.Read, true);
            try
            {
                var allowed = UpdateFiles
                    .Targets.Append("update.json")
                    .Append("update.sig")
                    .ToHashSet(StringComparer.Ordinal);
                if (zip.Entries.Count != allowed.Count)
                    throw new InvalidDataException("Unexpected update ZIP entries.");
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    expanded = checked(expanded + entry.Length);
                    if (
                        !allowed.Remove(entry.FullName)
                        || entry.Length is < 1 or > 64 * 1024 * 1024
                        || expanded > 96 * 1024 * 1024
                        || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                        || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                    )
                        throw new InvalidDataException("Unsafe update ZIP entry.");
                }
                var manifestBytes = ReadMetadata("update.json");
                var signatureEntry = zip.GetEntry("update.sig")!;
                if (signatureEntry.Length != 64)
                    throw new InvalidDataException(
                        "Update signature must contain exactly 64 bytes."
                    );
                UpdateSignature.Verify(manifestBytes, ReadMetadata("update.sig"));
                Manifest =
                    JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, UpdateFiles.Json)
                    ?? throw new InvalidDataException("Missing update manifest.");
                if (
                    Manifest.Schema != 1
                    || Manifest.RecoveryContract != UpdateFiles.RecoveryContract
                    || Manifest.Files is null
                    || Manifest.Files.Length != 3
                    || !Manifest.Files.Select(f => f.Path).SequenceEqual(UpdateFiles.Targets)
                )
                    throw new InvalidDataException("Unsupported update contract.");
                UpdateFiles.Version(Manifest.Version);
                foreach (var stamp in Manifest.Files)
                {
                    UpdateFiles.ValidateStamp(stamp);
                    var entry = zip.GetEntry(stamp.Path)!;
                    using var input = entry.Open();
                    if (
                        entry.Length != stamp.Size
                        || UpdateFiles.Hash(input, stamp.Size) != stamp.Sha256
                    )
                        throw new InvalidDataException("Update file digest mismatch.");
                }
                using var certificate = JsonDocument.Parse(ReadMetadata(UpdateFiles.Certificate));
                Certificate = certificate.RootElement.Clone();
                if (
                    Certificate.GetProperty("capture_version").GetString() != Manifest.Version
                    || Certificate.GetProperty("update_mode").GetString() != "automatic"
                    || Certificate.GetProperty("recovery_contract").GetString()
                        != Manifest.RecoveryContract
                )
                    throw new InvalidDataException("Update certificate identity mismatch.");
                var plugin = zip.GetEntry(UpdateFiles.Targets[0])!;
                using var pluginBytes = new MemoryStream();
                using (var input = plugin.Open())
                    UpdateFiles.CopyExact(input, pluginBytes, plugin.Length);
                pluginBytes.Position = 0;
                using var pe = new PEReader(pluginBytes);
                var assemblyVersion = pe.GetMetadataReader().GetAssemblyDefinition().Version;
                if (assemblyVersion != new Version(Manifest.Version + ".0"))
                    throw new InvalidDataException("Update assembly version differs from release.");
                var records = RuntimeFiles(Certificate);
                foreach (var stamp in Manifest.Files.Take(2))
                    if (!records.TryGetValue(stamp.Path, out var runtime) || stamp != runtime)
                        throw new InvalidDataException(
                            "Capture file absent from runtime certificate."
                        );
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private byte[] ReadMetadata(string name)
    {
        var entry = zip.GetEntry(name)!;
        if (entry.Length > UpdateFiles.MetadataLimit)
            throw new InvalidDataException("Update metadata is too large.");
        using var stream = entry.Open();
        var data = new byte[checked((int)entry.Length)];
        stream.ReadExactly(data);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Update entry length mismatch.");
        return data;
    }

    internal static Dictionary<string, FileStamp> RuntimeFiles(JsonElement certificate)
    {
        if (
            certificate.GetProperty("schema").GetInt32() != 1
            || certificate.GetProperty("runtime_major").GetInt32() != 10
        )
            throw new InvalidDataException("Unsupported capture runtime certificate.");
        var records = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in certificate.GetProperty("runtime_files").EnumerateArray())
        {
            var stamp = JsonSerializer.Deserialize<FileStamp>(entry, UpdateFiles.Json)!;
            UpdateFiles.ValidateStamp(stamp);
            if (!records.TryAdd(stamp.Path, stamp))
                throw new InvalidDataException("Duplicate runtime file.");
        }
        return records;
    }

    internal Version CheckInstalled(UpdateFiles files)
    {
        using var document = JsonDocument.Parse(
            UpdateFiles.ReadSmall(files.Resolve(UpdateFiles.Certificate))
        );
        var current = document.RootElement;
        foreach (
            var field in new[]
            {
                "schema",
                "runtime_major",
                "runtime_version",
                "update_mode",
                "recovery_contract",
                "supported_client",
                "configuration_contract",
            }
        )
            if (!JsonElement.DeepEquals(current.GetProperty(field), Certificate.GetProperty(field)))
                throw new InvalidDataException(
                    "Update needs a different base runtime or recovery contract; install the full package."
                );
        var before = RuntimeFiles(current);
        var after = RuntimeFiles(Certificate);
        var baseBefore = before
            .Values.Where(s => !UpdateFiles.Targets.Contains(s.Path))
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToArray();
        var baseAfter = after
            .Values.Where(s => !UpdateFiles.Targets.Contains(s.Path))
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToArray();
        if (baseBefore.Length == 0 || !baseBefore.SequenceEqual(baseAfter))
            throw new InvalidDataException("Update base changed; install the full package.");
        foreach (var stamp in before.Values)
            if (!UpdateFiles.Matches(files.Resolve(stamp.Path), stamp))
                throw new InvalidDataException(
                    "Installed runtime file changed; automatic update deferred."
                );
        var version = UpdateFiles.Version(current.GetProperty("capture_version").GetString()!);
        return version;
    }

    internal void Extract(UpdateFiles files, string directory)
    {
        files.Ordinary(directory);
        Directory.CreateDirectory(directory);
        foreach (var stamp in Manifest.Files)
        {
            var destination = Path.Combine(directory, Path.GetFileName(stamp.Path));
            files.Ordinary(destination);
            using var source = zip.GetEntry(stamp.Path)!.Open();
            using (
                var output = new FileStream(
                    destination,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    65536
                )
            )
            {
                UpdateFiles.CopyExact(source, output, stamp.Size);
                output.Flush(true);
            }
            if (!UpdateFiles.Matches(destination, stamp))
                throw new InvalidDataException("Extracted update changed.");
        }
    }

    public void Dispose()
    {
        zip.Dispose();
        file.Dispose();
    }
}
