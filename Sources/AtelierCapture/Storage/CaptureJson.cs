using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AtelierCapture.Storage;

internal static class CaptureJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = int.MaxValue,
        Converters = { new ByteArrayWriter(), new StringWriter(), new NodeWriter() },
    };

    internal static void Write(Stream output, object value)
    {
        using var buffer = new StreamingBuffer(output);
        using var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { MaxDepth = int.MaxValue }
        );
        JsonSerializer.Serialize(writer, value, Options);
    }

    internal static void WriteLine(Stream output, object value)
    {
        Write(output, value);
        output.WriteByte((byte)'\n');
    }

    private sealed class NodeWriter : JsonConverter<JsonNode>
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeof(JsonNode).IsAssignableFrom(typeToConvert);

        public override JsonNode? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            JsonNode value,
            JsonSerializerOptions options
        ) => WriteNode(writer, value);
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? value)
    {
        var pending = new Stack<(JsonNode? Node, int Index)>();
        pending.Push((value, -1));
        while (pending.TryPop(out var frame))
        {
            JsonNode? child;
            if (frame.Node is JsonObject obj)
            {
                if (frame.Index < 0)
                {
                    writer.WriteStartObject();
                    pending.Push((obj, 0));
                    continue;
                }
                if (frame.Index == obj.Count)
                {
                    writer.WriteEndObject();
                    continue;
                }
                var property = obj.GetAt(frame.Index);
                writer.WritePropertyName(property.Key);
                child = property.Value;
                pending.Push((obj, frame.Index + 1));
            }
            else if (frame.Node is JsonArray array)
            {
                if (frame.Index < 0)
                {
                    writer.WriteStartArray();
                    pending.Push((array, 0));
                    continue;
                }
                if (frame.Index == array.Count)
                {
                    writer.WriteEndArray();
                    continue;
                }
                child = array[frame.Index];
                pending.Push((array, frame.Index + 1));
            }
            else
            {
                if (frame.Node is null)
                    writer.WriteNullValue();
                else if (frame.Node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    WriteString(writer, text);
                else
                    frame.Node.WriteTo(writer);
                continue;
            }
            pending.Push((child, -1));
        }
    }

    private sealed class ByteArrayWriter : JsonConverter<byte[]>
    {
        public override byte[] Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            byte[] value,
            JsonSerializerOptions options
        )
        {
            if (value.Length == 0)
            {
                writer.WriteBase64StringSegment([], true);
                return;
            }
            for (var offset = 0; offset < value.Length; )
            {
                var count = Math.Min(65536, value.Length - offset);
                writer.WriteBase64StringSegment(
                    value.AsSpan(offset, count),
                    offset + count == value.Length
                );
                writer.Flush();
                offset += count;
            }
        }
    }

    private sealed class StringWriter : JsonConverter<string>
    {
        public override string? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            string value,
            JsonSerializerOptions options
        ) => WriteString(writer, value);
    }

    private static void WriteString(Utf8JsonWriter writer, string value)
    {
        if (value.Length <= 8192)
        {
            writer.WriteStringValue(value);
            return;
        }
        for (var offset = 0; offset < value.Length; )
        {
            var count = Math.Min(8192, value.Length - offset);
            writer.WriteStringValueSegment(
                value.AsSpan(offset, count),
                offset + count == value.Length
            );
            writer.Flush();
            offset += count;
        }
    }

    private sealed class StreamingBuffer(Stream output) : IBufferWriter<byte>, IDisposable
    {
        private byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);

        public void Advance(int count)
        {
            if (count < 0 || count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            output.Write(buffer.AsSpan(0, count));
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
            }
            return buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            buffer = [];
        }
    }
}

internal sealed class CountOutput(Stream destination) : Stream
{
    internal long Bytes { get; private set; }
    internal long Records { get; private set; }

    public override void Write(ReadOnlySpan<byte> data)
    {
        destination.Write(data);
        Bytes = checked(Bytes + data.Length);
        Records = checked(Records + data.Count((byte)'\n'));
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value)
    {
        destination.WriteByte(value);
        Bytes = checked(Bytes + 1);
        if (value == (byte)'\n')
            Records = checked(Records + 1);
    }

    public override void Flush() => destination.Flush();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Bytes;
    public override long Position
    {
        get => Bytes;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
