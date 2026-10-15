namespace AtelierCapture.Storage;

internal sealed class CompressedMember : Stream
{
    private const int BlockSize = 65536;
    private readonly List<byte[]> blocks = [];
    private long length;

    internal long Records { get; set; }
    internal long ExtractedBytes { get; set; }
    internal long RetainedCapacity => (long)blocks.Count * BlockSize;

    public override void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var index = checked((int)(length / BlockSize));
            var offset = (int)(length % BlockSize);
            if (index == blocks.Count)
                blocks.Add(new byte[BlockSize]);
            var count = Math.Min(data.Length, BlockSize - offset);
            data[..count].CopyTo(blocks[index].AsSpan(offset));
            length = checked(length + count);
            data = data[count..];
        }
    }

    internal void Visit(long offset, Action<ReadOnlyMemory<byte>> read)
    {
        if (offset < 0 || offset > length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        while (offset < length)
        {
            var index = checked((int)(offset / BlockSize));
            var at = (int)(offset % BlockSize);
            var count = (int)Math.Min(length - offset, BlockSize - at);
            read(blocks[index].AsMemory(at, count));
            offset += count;
        }
    }

    internal void VerifySuffix(FileStream file, long committedLength)
    {
        var suffix = checked(file.Length - committedLength);
        if (suffix < 0 || suffix > length)
            throw new InvalidDataException(
                "Capture stream length conflicts with its retained candidate."
            );
        file.Position = committedLength;
        if (suffix == 0)
            return;
        var scratch = new byte[BlockSize];
        long compared = 0;
        Visit(
            0,
            expected =>
            {
                var count = (int)Math.Min(expected.Length, suffix - compared);
                if (count <= 0)
                    return;
                file.ReadExactly(scratch.AsSpan(0, count));
                if (!scratch.AsSpan(0, count).SequenceEqual(expected.Span[..count]))
                    throw new InvalidDataException(
                        "Capture stream suffix differs from its retained candidate."
                    );
                compared += count;
            }
        );
        if (compared != suffix)
            throw new InvalidDataException("Capture stream suffix could not be verified.");
        file.Position = file.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            blocks.Clear();
        }
        base.Dispose(disposing);
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Flush() { }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position
    {
        get => length;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
