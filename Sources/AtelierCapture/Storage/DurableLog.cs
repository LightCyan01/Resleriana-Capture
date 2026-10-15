using System.Security.Cryptography;

namespace AtelierCapture.Storage;

internal sealed class DurableLog(string path) : IDisposable
{
    private FileStream? file;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long hashedBytes;

    internal string Path { get; } = path;
    internal long Bytes { get; private set; }
    internal long Records { get; private set; }
    internal long ExtractedBytes { get; private set; }
    internal string Sha256 { get; private set; } = Convert.ToHexString(SHA256.HashData([]));

    private FileStream Open()
    {
        if (file is not null)
            return file;
        file = new FileStream(
            Path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            65536
        );
        if (file.Length < Bytes)
            throw new InvalidDataException("Capture stream is shorter than its committed prefix.");
        if (Bytes != 0)
        {
            using var verify = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var scratch = new byte[65536];
            var remaining = Bytes;
            while (remaining != 0)
            {
                var count = file.Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, remaining)));
                if (count == 0)
                    throw new InvalidDataException("Capture committed prefix is truncated.");
                verify.AppendData(scratch.AsSpan(0, count));
                remaining -= count;
            }
            if (
                !Convert
                    .ToHexString(verify.GetHashAndReset())
                    .Equals(Sha256, StringComparison.Ordinal)
            )
                throw new InvalidDataException(
                    "Capture committed prefix changed while storage was unavailable."
                );
        }
        return file;
    }

    internal void AppendAndFlush(CompressedMember member, StorageHooks? hooks)
    {
        var output = Open();
        member.VerifySuffix(output, Bytes);
        var present = output.Length - Bytes;
        hooks?.Before?.Invoke(StorageAction.Append, Path, present);
        member.Visit(
            present,
            data =>
            {
                if (hooks?.Append is { } append)
                    append(output, data);
                else
                    output.Write(data.Span);
            }
        );
        hooks?.After?.Invoke(StorageAction.Append, Path, output.Length);
        hooks?.Before?.Invoke(StorageAction.DataFlush, Path, output.Length);
        output.Flush(flushToDisk: true);
        hooks?.After?.Invoke(StorageAction.DataFlush, Path, output.Length);
    }

    internal StorageStreamStamp CandidateStamp(CompressedMember? member)
    {
        if (member is null)
            return new(System.IO.Path.GetFileName(Path), Bytes, Sha256, Records, ExtractedBytes);
        if (hashedBytes != Bytes && hashedBytes != checked(Bytes + member.Length))
            throw new InvalidDataException(
                "Capture hash state differs from its candidate boundary."
            );
        if (hashedBytes == Bytes)
        {
            member.Visit(0, data => hash.AppendData(data.Span));
            hashedBytes = checked(Bytes + member.Length);
        }
        return new(
            System.IO.Path.GetFileName(Path),
            hashedBytes,
            Convert.ToHexString(hash.GetCurrentHash()),
            checked(Records + member.Records),
            checked(ExtractedBytes + member.ExtractedBytes)
        );
    }

    internal void Commit(StorageStreamStamp stamp)
    {
        Bytes = stamp.Bytes;
        Records = stamp.Records;
        ExtractedBytes = stamp.ExtractedBytes;
        Sha256 = stamp.Sha256;
    }

    internal void ReopenAfterFailure()
    {
        try
        {
            file?.Dispose();
        }
        finally
        {
            file = null;
        }
    }

    public void Dispose()
    {
        try
        {
            file?.Dispose();
        }
        finally
        {
            hash.Dispose();
        }
    }
}
