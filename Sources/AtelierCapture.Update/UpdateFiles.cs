using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtelierCapture.Update;

internal sealed record FileStamp(string Path, long Size, string Sha256);

internal sealed record UpdateManifest(
    int Schema,
    string Version,
    string RecoveryContract,
    FileStamp[] Files
);

internal sealed record UpdateJournal(int Schema, FileStamp[] Before, FileStamp[] After);

internal sealed class UpdateFiles
{
    internal const string Certificate = "BepInEx/plugins/capture-runtime.json";
    internal const string RecoveryContract = "capture5-checkpoint2";
    internal const string GateKey = "AtelierCapture.UpdateReady";
    internal const string Repository = "LightCyan01/Resleriana-Capture";
    internal const int MetadataLimit = 1024 * 1024;
    internal static readonly string[] Targets =
    [
        "BepInEx/plugins/AtelierCapture.dll",
        "BepInEx/plugins/AtelierCapture.Native.dll",
        Certificate,
    ];
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    internal string Root { get; }
    internal string State { get; }
    internal string Ready => Resolve("BepInEx/capture-update/ready.zip");
    internal string Journal => Resolve("BepInEx/capture-update/transaction.json");

    internal UpdateFiles(string root)
    {
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        State = Resolve("BepInEx/capture-update");
    }

    internal string Resolve(string relative)
    {
        if (
            string.IsNullOrWhiteSpace(relative)
            || relative.Contains('\\')
            || relative.Contains(':')
            || relative.StartsWith('/')
            || relative.Split('/').Any(p => p is "" or "." or "..")
        )
            throw new InvalidDataException("Invalid update path.");
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));
        if (
            !path.StartsWith(
                Root + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidDataException("Update path escapes game directory.");
        Ordinary(path);
        return path;
    }

    internal void Ordinary(string path)
    {
        for (
            var current = path;
            current is not null;
            current = System.IO.Path.GetDirectoryName(current)
        )
        {
            if (
                (File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
            )
                throw new InvalidDataException("Update paths must not be links or junctions.");
            if (string.Equals(current, Root, StringComparison.OrdinalIgnoreCase))
                return;
        }
        throw new InvalidDataException("Update path is outside game directory.");
    }

    internal void Initialize()
    {
        Ordinary(State);
        Directory.CreateDirectory(State);
    }

    internal static string Hash(Stream stream) => Hash(stream, stream.Length);

    internal static string Hash(Stream stream, long expected)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            total = checked(total + count);
            if (total > expected)
                throw new InvalidDataException("Update expanded length exceeds its declared size.");
            hash.AppendData(buffer, 0, count);
        }
        if (total != expected)
            throw new InvalidDataException(
                "Update expanded length differs from its declared size."
            );
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void CopyExact(Stream source, Stream output, long expected)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            total = checked(total + count);
            if (total > expected)
                throw new InvalidDataException("Update expanded length exceeds its declared size.");
            output.Write(buffer, 0, count);
        }
        if (total != expected)
            throw new InvalidDataException(
                "Update expanded length differs from its declared size."
            );
    }

    internal static FileStamp Stamp(string relative, string path)
    {
        using var input = File.OpenRead(path);
        return new(relative, input.Length, Hash(input));
    }

    internal static bool Matches(string path, FileStamp stamp)
    {
        if (!File.Exists(path))
            return false;
        using var input = File.OpenRead(path);
        return input.Length == stamp.Size && Hash(input) == stamp.Sha256;
    }

    internal static void ValidateStamp(FileStamp stamp)
    {
        if (
            stamp is null
            || stamp.Size is < 1 or > 96 * 1024 * 1024
            || stamp.Sha256 is null
            || stamp.Sha256.Length != 64
            || !stamp.Sha256.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c))
        )
            throw new InvalidDataException("Invalid update file identity.");
    }

    internal static byte[] ReadSmall(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length is < 1 or > MetadataLimit)
            throw new InvalidDataException("Invalid update metadata length.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    internal T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(ReadSmall(path), Json)
        ?? throw new InvalidDataException("Empty update metadata.");

    internal void Write<T>(string path, T value)
    {
        Ordinary(path);
        var temporary = path + ".new";
        Ordinary(temporary);
        using (
            var output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            )
        )
        {
            JsonSerializer.Serialize(output, value, Json);
            output.Flush(true);
        }
        MoveDurable(temporary, path);
    }

    internal void CopyDurable(string source, string target)
    {
        Ordinary(source);
        Ordinary(target);
        using var input = File.OpenRead(source);
        using var output = new FileStream(
            target,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            65536
        );
        input.CopyTo(output, 65536);
        output.Flush(true);
    }

    internal void Replace(string source, string target)
    {
        var temporary = target + ".capture-update";
        CopyDurable(source, temporary);
        MoveDurable(temporary, target);
    }

    private static void MoveDurable(string source, string target)
    {
        if (!MoveFileEx(source, target, 0x1 | 0x8))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Capture update rename failed.");
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string target, int flags);

    internal static Version Version(string text)
    {
        if (
            text is null
            || !System.Version.TryParse(text, out var version)
            || version.Build < 0
            || version.Revision != -1
            || version.ToString() != text
        )
            throw new InvalidDataException("Update version must be major.minor.patch.");
        return version;
    }

    internal bool Enabled()
    {
        var path = Resolve("BepInEx/config/AtelierCapture.Updater.cfg");
        if (!File.Exists(path))
            return true;
        if (new FileInfo(path).Length > 65536)
            return false;
        var section = "";
        bool? value = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                section = line[1..^1];
            if (section != "Updates" || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0] != "Enabled")
                continue;
            if (value is not null || !bool.TryParse(parts[1], out var parsed))
                return false;
            value = parsed;
        }
        return value ?? false;
    }
}
