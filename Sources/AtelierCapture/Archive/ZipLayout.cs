using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AtelierCapture.Archive;

internal static class ZipLayout
{
    internal static void Verify(string path, ZipArchive zip)
    {
        using var file = File.OpenRead(path);
        Verify(file, zip);
    }

    internal static void Verify(FileStream file, ZipArchive zip)
    {
        _ = zip.Entries.Count;
        var tail = new byte[(int)Math.Min(file.Length, 65557)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (
                U32(tail.AsSpan(i)) == 0x06054B50
                && i + 22 + U16(tail.AsSpan(i + 20)) == tail.Length
            )
            {
                end = i;
                break;
            }
        if (end < 0)
            throw new InvalidDataException("Capture ZIP end directory is missing.");
        var eocd = tail.AsSpan(end);
        if (U16(eocd[4..]) != 0 || U16(eocd[6..]) != 0 || U16(eocd[8..]) != U16(eocd[10..]))
            throw new InvalidDataException("Capture ZIP is split across disks.");
        long count = U16(eocd[10..]),
            bytes = U32(eocd[12..]),
            offset = U32(eocd[16..]);
        var endOffset = file.Length - tail.Length + end;
        if (count == ushort.MaxValue || bytes == uint.MaxValue || offset == uint.MaxValue)
        {
            var locator = ReadAt(file, endOffset - 20, 20);
            if (
                U32(locator) != 0x07064B50
                || U32(locator.AsSpan(4)) != 0
                || U32(locator.AsSpan(16)) != 1
            )
                throw new InvalidDataException("Capture ZIP64 locator is invalid.");
            var position = I64(locator.AsSpan(8));
            var record = ReadAt(file, position, 56);
            if (
                U32(record) != 0x06064B50
                || I64(record.AsSpan(4)) < 44
                || U32(record.AsSpan(16)) != 0
                || U32(record.AsSpan(20)) != 0
                || I64(record.AsSpan(24)) != I64(record.AsSpan(32))
            )
                throw new InvalidDataException("Capture ZIP64 directory is invalid.");
            count = I64(record.AsSpan(32));
            bytes = I64(record.AsSpan(40));
            offset = I64(record.AsSpan(48));
        }
        if (count != 5 || offset < 0 || bytes < 0 || checked(offset + bytes) > endOffset)
            throw new InvalidDataException("Capture ZIP directory size/count is invalid.");
        file.Position = offset;
        var owned = new List<(long Start, long End)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var header = new byte[46];
            file.ReadExactly(header);
            if (U32(header) != 0x02014B50)
                throw new InvalidDataException("Capture ZIP central header is invalid.");
            var flags = U16(header.AsSpan(8));
            var method = U16(header.AsSpan(10));
            if ((flags & 1) != 0 || method is not (0 or 8) || U16(header.AsSpan(34)) != 0)
                throw new InvalidDataException(
                    "Capture ZIP compression/encryption/disk is unsupported."
                );
            var nameBytes = new byte[U16(header.AsSpan(28))];
            file.ReadExactly(nameBytes);
            var extra = new byte[U16(header.AsSpan(30))];
            file.ReadExactly(extra);
            var comment = U16(header.AsSpan(32));
            file.Seek(comment, SeekOrigin.Current);
            var name = new UTF8Encoding(false, true).GetString(nameBytes);
            if (
                !names.Add(name)
                || name.Contains('/')
                || name.Contains('\\')
                || !ArchiveCounts.Names.Append("manifest.json").Contains(name)
            )
                throw new InvalidDataException("Capture ZIP directory names are invalid.");
            long expanded = U32(header.AsSpan(24)),
                compressed = U32(header.AsSpan(20)),
                local = U32(header.AsSpan(42));
            ReadZip64(extra, ref expanded, ref compressed, ref local);
            var stored = zip.GetEntry(name)!;
            if (
                expanded != stored.Length
                || compressed != stored.CompressedLength
                || U32(header.AsSpan(16)) != stored.Crc32
            )
                throw new InvalidDataException("Capture ZIP directory facts differ.");
            var nextCentral = file.Position;
            var localHeader = ReadAt(file, local, 30);
            if (
                U32(localHeader) != 0x04034B50
                || U16(localHeader.AsSpan(6)) != flags
                || U16(localHeader.AsSpan(8)) != method
                || U16(localHeader.AsSpan(26)) != nameBytes.Length
            )
                throw new InvalidDataException("Capture ZIP local header differs.");
            var localName = ReadAt(file, checked(local + 30), nameBytes.Length);
            if (!localName.AsSpan().SequenceEqual(nameBytes))
                throw new InvalidDataException("Capture ZIP local name differs.");
            var data = checked(local + 30 + nameBytes.Length + U16(localHeader.AsSpan(28)));
            long localExpanded = U32(localHeader.AsSpan(22)),
                localCompressed = U32(localHeader.AsSpan(18)),
                unusedOffset = 0;
            if ((flags & 8) == 0)
            {
                var localExtra = ReadAt(
                    file,
                    checked(local + 30 + nameBytes.Length),
                    U16(localHeader.AsSpan(28))
                );
                ReadZip64(localExtra, ref localExpanded, ref localCompressed, ref unusedOffset);
                if (
                    localExpanded != expanded
                    || localCompressed != compressed
                    || U32(localHeader.AsSpan(14)) != stored.Crc32
                )
                    throw new InvalidDataException(
                        "Capture ZIP local sizes/CRC differ from its directory."
                    );
            }
            var lastByte = checked(data + compressed);
            if (lastByte > offset)
                throw new InvalidDataException("Capture ZIP entry overlaps its directory.");
            owned.Add((local, lastByte));
            file.Position = nextCentral;
        }
        if (file.Position != checked(offset + bytes))
            throw new InvalidDataException("Capture ZIP directory length differs.");
        owned.Sort((left, right) => left.Start.CompareTo(right.Start));
        if (
            owned[0].Start != 0
            || owned.Zip(owned.Skip(1)).Any(pair => pair.First.End > pair.Second.Start)
        )
            throw new InvalidDataException("Capture ZIP contains overlapping/prefixed entries.");
    }

    private static void ReadZip64(
        byte[] extra,
        ref long expanded,
        ref long compressed,
        ref long local
    )
    {
        var found = false;
        var needed =
            expanded == uint.MaxValue || compressed == uint.MaxValue || local == uint.MaxValue;
        for (var offset = 0; offset < extra.Length; )
        {
            if (extra.Length - offset < 4)
                throw new InvalidDataException("Capture ZIP extra field is truncated.");
            var tag = U16(extra.AsSpan(offset));
            var length = U16(extra.AsSpan(offset + 2));
            offset += 4;
            if (length > extra.Length - offset)
                throw new InvalidDataException("Capture ZIP extra field length differs.");
            if (tag == 1)
            {
                if (found)
                    throw new InvalidDataException("Capture ZIP64 field is duplicated.");
                found = true;
                var data = extra.AsSpan(offset, length);
                var cursor = 0;
                if (expanded == uint.MaxValue)
                    expanded = Next(data, ref cursor);
                if (compressed == uint.MaxValue)
                    compressed = Next(data, ref cursor);
                if (local == uint.MaxValue)
                    local = Next(data, ref cursor);
            }
            offset += length;
        }
        if (needed && !found)
            throw new InvalidDataException("Capture ZIP64 field is missing.");
    }

    private static long Next(ReadOnlySpan<byte> data, ref int cursor)
    {
        if (data.Length - cursor < 8)
            throw new InvalidDataException("Capture ZIP64 size is truncated.");
        var value = I64(data[cursor..]);
        cursor += 8;
        return value;
    }

    private static byte[] ReadAt(FileStream file, long offset, int count)
    {
        if (offset < 0 || offset > file.Length - count)
            throw new InvalidDataException("Capture ZIP offset is invalid.");
        file.Position = offset;
        var result = new byte[count];
        file.ReadExactly(result);
        return result;
    }

    private static ushort U16(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data);

    private static uint U32(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data);

    private static long I64(ReadOnlySpan<byte> data)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data);
        if (value > long.MaxValue)
            throw new InvalidDataException("Capture ZIP64 integer is outside signed file bounds.");
        return (long)value;
    }
}
