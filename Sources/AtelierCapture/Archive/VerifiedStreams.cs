using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AtelierCapture.Archive;

internal sealed class GzipPrefix : Stream
{
    // An empty member forces incomplete trailers through the inflater's CRC/size checks.
    private static readonly byte[] EmptyMember =
    [
        31,
        139,
        8,
        0,
        0,
        0,
        0,
        0,
        0,
        3,
        3,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
    ];
    private readonly FileStream file;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly string expectedHash;
    private long remaining;
    private int sentinel;

    internal GzipPrefix(string path, long bytes, string sha256)
    {
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        if (bytes < 20 || bytes > file.Length)
        {
            file.Dispose();
            throw new InvalidDataException("Capture gzip prefix is missing or truncated.");
        }
        remaining = bytes;
        expectedHash = sha256;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;
        if (remaining != 0)
        {
            var count = file.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            if (count == 0)
                throw new InvalidDataException("Capture committed gzip prefix is truncated.");
            remaining -= count;
            hash.AppendData(buffer[..count]);
            return count;
        }
        var copied = Math.Min(buffer.Length, EmptyMember.Length - sentinel);
        EmptyMember.AsSpan(sentinel, copied).CopyTo(buffer);
        sentinel += copied;
        return copied;
    }

    internal void Verify()
    {
        if (
            remaining != 0
            || sentinel != EmptyMember.Length
            || !Convert
                .ToHexString(hash.GetHashAndReset())
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidDataException("Capture compressed prefix hash/framing differs.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            file.Dispose();
            hash.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

internal sealed class StreamFacts(Stream input, Stream? output = null) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private uint crc = uint.MaxValue;
    private static readonly uint[] CrcTable = MakeTable();
    internal long Bytes { get; private set; }
    internal long Records { get; private set; }
    internal uint Crc32 => ~crc;
    internal string Sha256 => Convert.ToHexString(hash.GetCurrentHash());

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = input.Read(buffer);
        var data = buffer[..count];
        Append(data);
        return count;
    }

    internal void Append(ReadOnlySpan<byte> data)
    {
        output?.Write(data);
        hash.AppendData(data);
        Bytes = checked(Bytes + data.Length);
        Records = checked(Records + data.Count((byte)10));
        crc = Crc(crc, data);
    }

    // Slicing-by-8: the byte-at-a-time table loop was most of the archive hashing time.
    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        var t = CrcTable;
        for (; data.Length >= 8; data = data[8..])
        {
            var low = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            var high = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc =
                t[1792 + (low & 255)]
                ^ t[1536 + ((low >> 8) & 255)]
                ^ t[1280 + ((low >> 16) & 255)]
                ^ t[1024 + (low >> 24)]
                ^ t[768 + (high & 255)]
                ^ t[512 + ((high >> 8) & 255)]
                ^ t[256 + ((high >> 16) & 255)]
                ^ t[high >> 24];
        }
        foreach (var value in data)
            crc = t[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeTable()
    {
        var result = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320);
            result[i] = value;
        }
        for (var i = 256; i < result.Length; i++)
            result[i] = (result[i - 256] >> 8) ^ result[result[i - 256] & 255];
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            hash.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
