namespace AtelierCapture;

internal static class Wire
{
    private static readonly Lazy<Dictionary<string, Dictionary<int, string>>> Schemas = new(() =>
        Plugin.ReadResource<Dictionary<string, Dictionary<int, string>>>("WireSchemas.json"));

    internal static object Describe(byte[] bytes, string? type)
    {
        var name = type?.Replace("Blend.ApiSchema.Api.", "blend.api.", StringComparison.Ordinal)
            .Replace("Google.Protobuf.WellKnownTypes.", "google.protobuf.", StringComparison.Ordinal);
        var unknown = new List<object>();
        var scanned = 0;
        var truncated = false;
        string? error = null;
        try { Scan(0, bytes.Length, name, "", 0); }
        catch (InvalidDataException) { error = "invalid_protobuf_wire"; }
        return new { fields_scanned = scanned, unknown_fields = unknown, truncated, error };

        void Scan(int start, int end, string? message, string path, int depth)
        {
            if (depth > 32) { truncated = true; return; }
            Dictionary<int, string>? fields = null;
            if (message is not null) Schemas.Value.TryGetValue(message, out fields);
            for (var offset = start; offset < end;)
            {
                if (++scanned > 65536) { truncated = true; return; }
                var tag = Varint(bytes, ref offset, end);
                if (tag > uint.MaxValue || tag >> 3 == 0) throw new InvalidDataException();
                var number = (int)(tag >> 3);
                var wire = (int)(tag & 7);
                var valueStart = offset;
                var length = 0;
                switch (wire)
                {
                    case 0: Varint(bytes, ref offset, end); break;
                    case 1: length = 8; break;
                    case 2:
                        var count = Varint(bytes, ref offset, end);
                        if (count > int.MaxValue) throw new InvalidDataException();
                        length = (int)count;
                        valueStart = offset;
                        break;
                    case 5: length = 4; break;
                    default: throw new InvalidDataException();
                }
                if (length > end - offset) throw new InvalidDataException();
                offset += length;
                var fieldPath = path.Length == 0 ? number.ToString() : path + "." + number;
                if (fields is null || !fields.TryGetValue(number, out var nested))
                {
                    if (unknown.Count < 128)
                        unknown.Add(new { path = fieldPath, field_number = number, wire_type = wire, bytes = offset - valueStart });
                    else truncated = true;
                }
                else if (wire == 2 && nested.Length != 0)
                    Scan(valueStart, offset, nested, fieldPath, depth + 1);
            }
        }
    }

    private static ulong Varint(byte[] data, ref int offset, int end)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset == end) throw new InvalidDataException();
            var next = data[offset++];
            if (shift == 63 && next > 1) throw new InvalidDataException();
            value |= (ulong)(next & 127) << shift;
            if (next < 128) return value;
        }
        throw new InvalidDataException();
    }
}
