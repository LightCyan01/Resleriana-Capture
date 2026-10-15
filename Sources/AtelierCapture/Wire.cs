namespace AtelierCapture;

internal static class Wire
{
    private static readonly Lazy<Dictionary<string, Dictionary<int, string>>> Schemas = new(() =>
        Plugin.ReadResource<Dictionary<string, Dictionary<int, string>>>("WireSchemas.json")
    );

    internal sealed record UnknownField(
        string path,
        int field_number,
        int wire_type,
        int offset,
        int bytes
    );

    internal sealed record Description(
        long fields_scanned,
        long unknown_count,
        IReadOnlyList<UnknownField> unknown_fields,
        bool summary_limited,
        string? error,
        bool original_bytes_required = true
    );

    internal static Description Describe(byte[] bytes, string? type) =>
        Describe(bytes, type, Schemas.Value);

    internal static Description Describe(
        byte[] bytes,
        string? type,
        IReadOnlyDictionary<string, Dictionary<int, string>> schemas
    )
    {
        var name = type
            ?.Replace("Blend.ApiSchema.Api.", "blend.api.", StringComparison.Ordinal)
            .Replace("Blend.ApiSchema.Model.", "blend.model.", StringComparison.Ordinal)
            .Replace(
                "Google.Protobuf.WellKnownTypes.",
                "google.protobuf.",
                StringComparison.Ordinal
            );
        var unknown = new List<UnknownField>();
        long scanned = 0,
            unknownCount = 0;
        var frames = new Stack<Frame>();
        frames.Push(new Frame(0, bytes.Length, name, null, null));
        try
        {
            while (frames.TryPop(out var frame))
            {
                var offset = frame.Offset;
                var endedGroup = false;
                Dictionary<int, string>? fields = null;
                if (frame.Message is not null)
                    schemas.TryGetValue(frame.Message, out fields);
                while (offset < frame.End)
                {
                    var fieldStart = offset;
                    var tag = Varint(bytes, ref offset, frame.End);
                    if (tag > uint.MaxValue || tag >> 3 == 0)
                        throw new InvalidDataException();
                    var number = (int)(tag >> 3);
                    var wire = (int)(tag & 7);
                    if (wire == 4)
                    {
                        if (frame.Group != number)
                            throw new InvalidDataException();
                        var parent = frames.Pop();
                        frames.Push(parent with { Offset = offset });
                        endedGroup = true;
                        break;
                    }
                    scanned++;
                    var valueStart = offset;
                    var length = 0;
                    switch (wire)
                    {
                        case 0:
                            Varint(bytes, ref offset, frame.End);
                            break;
                        case 1:
                            length = 8;
                            break;
                        case 2:
                            var count = Varint(bytes, ref offset, frame.End);
                            if (count > int.MaxValue)
                                throw new InvalidDataException();
                            length = (int)count;
                            valueStart = offset;
                            break;
                        case 3:
                            break;
                        case 5:
                            length = 4;
                            break;
                        default:
                            throw new InvalidDataException();
                    }
                    if (length > frame.End - offset)
                        throw new InvalidDataException();
                    offset += length;
                    var known = fields is not null && fields.TryGetValue(number, out _);
                    var nested = known ? fields![number] : null;
                    if (!known)
                    {
                        unknownCount++;
                        if (unknown.Count < 128)
                            unknown.Add(
                                new UnknownField(
                                    FormatPath(frame.Path, number),
                                    number,
                                    wire,
                                    fieldStart,
                                    offset - fieldStart
                                )
                            );
                    }
                    if (wire == 3)
                    {
                        frames.Push(frame with { Offset = offset });
                        frames.Push(
                            new Frame(
                                offset,
                                frame.End,
                                nested,
                                new PathNode(frame.Path, number),
                                number
                            )
                        );
                        break;
                    }
                    if (wire == 2 && !string.IsNullOrEmpty(nested))
                    {
                        frames.Push(frame with { Offset = offset });
                        frames.Push(
                            new Frame(
                                valueStart,
                                offset,
                                nested,
                                new PathNode(frame.Path, number),
                                null
                            )
                        );
                        break;
                    }
                }
                if (offset == frame.End && frame.Group is not null && !endedGroup)
                    throw new InvalidDataException();
            }
            return new Description(
                scanned,
                unknownCount,
                unknown,
                unknownCount > unknown.Count,
                null
            );
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return new Description(
                scanned,
                unknownCount,
                unknown,
                unknownCount > unknown.Count,
                "invalid_protobuf_wire"
            );
        }
    }

    private sealed record PathNode(PathNode? Parent, int Number);

    private sealed record Frame(int Offset, int End, string? Message, PathNode? Path, int? Group);

    private static string FormatPath(PathNode? path, int number)
    {
        var fields = new List<int> { number };
        for (var node = path; node is not null; node = node.Parent)
            fields.Add(node.Number);
        var text = new System.Text.StringBuilder();
        for (var index = fields.Count - 1; index >= 0; index--)
        {
            if (index != fields.Count - 1)
                text.Append('.');
            text.Append(fields[index].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private static ulong Varint(byte[] data, ref int offset, int end)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset == end)
                throw new InvalidDataException();
            var next = data[offset++];
            if (shift == 63 && next > 1)
                throw new InvalidDataException();
            value |= (ulong)(next & 127) << shift;
            if (next < 128)
                return value;
        }
        throw new InvalidDataException();
    }
}
