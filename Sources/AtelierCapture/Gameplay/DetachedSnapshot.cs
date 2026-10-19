using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AtelierCapture;

internal readonly record struct SnapshotMember(string Name, Func<object?> Read);

internal sealed record SnapshotSource(
    string Type,
    object Identity,
    IReadOnlyList<SnapshotMember> Fields,
    Func<IEnumerable<object?>>? Items = null,
    Func<byte[]>? Protobuf = null,
    IReadOnlyList<string>? Excluded = null,
    IReadOnlyList<string>? Unavailable = null,
    string? LifetimeId = null,
    Func<IReadOnlyList<SnapshotMember>>? ProtobufFallback = null,
    Func<IReadOnlyList<string>>? Lineage = null,
    string? PrivacyType = null,
    IReadOnlyDictionary<string, string>? Declarations = null,
    string? ElementType = null,
    Func<JsonNode?>? FilteredProtobuf = null,
    IReadOnlyList<string>? AssignableTypes = null
);

internal sealed class SnapshotNode(int id, string type)
{
    public int id { get; } = id;

    [JsonIgnore]
    public string type { get; } = type;
    public int contract_id { get; internal set; }
    public Dictionary<string, object?> fields { get; } = new(StringComparer.Ordinal);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object?>? items { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? protobuf { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? protobuf_sha256 { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? representation { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? filtered_protobuf { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PrivacyPayload.Omission>? privacy_omissions { get; internal set; }

    [JsonIgnore]
    internal string? PrivacyType { get; set; }

    [JsonIgnore]
    internal IReadOnlyDictionary<string, string>? Declarations { get; set; }

    [JsonIgnore]
    internal string? ElementType { get; set; }

    [JsonIgnore]
    internal bool FilteredProtobufQualified { get; set; }

    [JsonIgnore]
    internal IReadOnlyList<string>? AssignableTypes { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string>? excluded_fields { get; internal set; }

    [JsonIgnore]
    public IReadOnlyList<string>? unavailable_fields { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? lifetime_id { get; internal set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? lineage_failures { get; internal set; }
}

internal sealed record SnapshotContract(
    int id,
    string type,
    IReadOnlyList<string>? excluded_fields,
    IReadOnlyList<string>? unavailable_fields
);

internal sealed record SnapshotFailure(int? Node, string Field, string Error, string Details);

internal sealed record SnapshotEnum(
    string? name,
    string? underlying_type,
    string? value,
    [property: JsonIgnore] string DeclaredType
);

// Flat references keep both copying and JSON serialization stack safe.
internal sealed class DetachedSnapshot(
    Func<object, SnapshotSource> describe,
    Func<object, object?>? identity = null
)
{
    private readonly Dictionary<object, int> identities = new();
    private readonly List<SnapshotNode> nodes = [];
    private readonly List<SnapshotFailure> failures = [];
    private readonly Queue<(SnapshotNode Node, SnapshotSource Source)> pending = new();
    private readonly List<SnapshotContract> contracts = [];
    private readonly Dictionary<string, List<SnapshotContract>> typeContracts = new(
        StringComparer.Ordinal
    );

    internal IReadOnlyList<SnapshotNode> Nodes => nodes;
    internal IReadOnlyList<SnapshotFailure> Failures => failures;
    internal IReadOnlyList<SnapshotContract> Contracts => contracts;
    internal bool Complete =>
        failures.Count == 0
        && nodes.All(n =>
            n.unavailable_fields is not { Count: > 0 } && n.lineage_failures is not { Count: > 0 }
        );
    internal long RetainedBytes { get; private set; }

    internal object ReadFailure(string field, Exception error) => Failure(null, field, error);

    internal object? Read(object? value)
    {
        var root = Reference(value, null, "$root");
        while (pending.TryDequeue(out var next))
        {
            var node = next.Node;
            var source = next.Source;
            var contract = Contract(source);
            node.excluded_fields = contract.excluded_fields;
            node.unavailable_fields = contract.unavailable_fields;
            node.contract_id = contract.id;
            node.lifetime_id = source.LifetimeId;
            node.PrivacyType = source.PrivacyType;
            node.Declarations = source.Declarations;
            node.ElementType = source.ElementType;
            node.AssignableTypes = source.AssignableTypes;
            if (source.Lineage is { } readLineage)
            {
                try
                {
                    node.lineage_failures = readLineage();
                }
                catch (Exception error)
                {
                    Failure(node.id, "$lineage", error);
                }
            }
            var members = source.Fields;
            if (source.FilteredProtobuf is { } filtered)
            {
                try
                {
                    node.filtered_protobuf = PrivacyPayload.Project(
                        source.PrivacyType ?? source.Type,
                        filtered(),
                        out _,
                        out var omissions
                    );
                    node.privacy_omissions = omissions;
                    node.FilteredProtobufQualified = true;
                    RetainedBytes = checked(RetainedBytes + FilteredBytes(node.filtered_protobuf));
                    node.representation = node.filtered_protobuf is null
                        ? "privacy_protobuf_unavailable"
                        : "privacy_qualified_protobuf_json";
                }
                catch (Exception ex)
                {
                    Failure(node.id, "$privacy_protobuf", ex);
                    node.representation = "privacy_protobuf_unavailable";
                }
            }
            else if (source.Protobuf is { } readProtobuf)
            {
                try
                {
                    var protobuf = readProtobuf();
                    node.protobuf = protobuf;
                    node.protobuf_sha256 = Convert.ToHexString(SHA256.HashData(protobuf));
                    node.representation = "native_reserialized";
                    RetainedBytes = checked(RetainedBytes + protobuf.LongLength);
                }
                catch (Exception ex)
                {
                    Failure(node.id, "$protobuf", ex);
                    if (source.ProtobufFallback is { } fallback)
                    {
                        try
                        {
                            members = fallback();
                        }
                        catch (Exception fallbackError)
                        {
                            Failure(node.id, "$protobuf_fallback", fallbackError);
                        }
                    }
                }
            }
            foreach (var field in members)
            {
                try
                {
                    node.fields.Add(field.Name, Reference(field.Read(), node.id, field.Name));
                }
                catch (Exception ex)
                {
                    node.fields[field.Name] = Failure(node.id, field.Name, ex);
                }
            }
            if (source.Items is null)
                continue;
            node.items = [];
            try
            {
                foreach (var item in source.Items())
                    node.items.Add(Reference(item, node.id, null, node.items.Count));
            }
            catch (Exception ex)
            {
                Failure(node.id, ItemField(node.items.Count), ex);
            }
        }
        return root;
    }

    // Item field names are built only on failure; collections can hold many items.
    private static long FilteredBytes(JsonNode? root)
    {
        if (root is null)
            return 0;
        var pending = new Stack<JsonNode>();
        pending.Push(root);
        long bytes = 0;
        while (pending.TryPop(out var node))
        {
            bytes = checked(bytes + 128);
            if (node is JsonObject fields)
                foreach (var field in fields)
                {
                    bytes = checked(bytes + 64 + field.Key.Length * 2L);
                    if (field.Value is { } value)
                        pending.Push(value);
                }
            else if (node is JsonArray items)
                foreach (var item in items)
                {
                    bytes = checked(bytes + 16);
                    if (item is not null)
                        pending.Push(item);
                }
            else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                bytes = checked(bytes + text.Length * 2L);
        }
        return bytes;
    }

    private object? Reference(object? value, int? parent, string? field, int item = 0)
    {
        RetainedBytes = checked(RetainedBytes + 32);
        if (value is null)
            return null;
        if (value is NativeReference)
            return value;
        if (value is IntPtr or UIntPtr)
            return Failure(
                parent,
                field ?? ItemField(item),
                new InvalidDataException("Required native pointer has no semantic reader.")
            );
        if (value is string text)
        {
            RetainedBytes = checked(RetainedBytes + text.Length * 2L);
            return text;
        }
        if (value is float single)
            return new
            {
                value = float.IsFinite(single) ? (float?)single : null,
                bits = BitConverter
                    .SingleToUInt32Bits(single)
                    .ToString("X8", CultureInfo.InvariantCulture),
            };
        if (value is double real)
            return new
            {
                value = double.IsFinite(real) ? (double?)real : null,
                bits = BitConverter
                    .DoubleToUInt64Bits(real)
                    .ToString("X16", CultureInfo.InvariantCulture),
            };
        if (value is long signed)
            return signed.ToString(CultureInfo.InvariantCulture);
        if (value is ulong unsigned)
            return unsigned.ToString(CultureInfo.InvariantCulture);
        if (value.GetType().IsEnum)
            return new SnapshotEnum(
                value.ToString(),
                Enum.GetUnderlyingType(value.GetType()).FullName,
                Convert.ToString(
                    Convert.ChangeType(
                        value,
                        Enum.GetUnderlyingType(value.GetType()),
                        CultureInfo.InvariantCulture
                    ),
                    CultureInfo.InvariantCulture
                ),
                value.GetType().FullName!
            );
        if (value.GetType().IsPrimitive || value is decimal)
            return value;
        try
        {
            var knownIdentity = identity?.Invoke(value);
            if (
                knownIdentity is not null
                && identities.TryGetValue(knownIdentity, out var existing)
            )
                return new { reference = existing };
            var source = describe(value);
            var sourceIdentity = knownIdentity ?? source.Identity;
            if (identities.TryGetValue(sourceIdentity, out var previous))
                return new { reference = previous };
            var id = checked(nodes.Count + 1);
            var node = new SnapshotNode(id, source.Type);
            identities.Add(sourceIdentity, id);
            nodes.Add(node);
            pending.Enqueue((node, source));
            return new { reference = id };
        }
        catch (Exception ex)
        {
            return Failure(parent, field ?? ItemField(item), ex);
        }
    }

    private static string ItemField(int index) => "items[" + index + "]";

    private SnapshotContract Contract(SnapshotSource source)
    {
        if (!typeContracts.TryGetValue(source.Type, out var variants))
            typeContracts[source.Type] = variants = [];
        foreach (var candidate in variants)
            if (
                Same(candidate.excluded_fields, source.Excluded)
                && Same(candidate.unavailable_fields, source.Unavailable)
            )
                return candidate;
        var contract = new SnapshotContract(
            checked(contracts.Count + 1),
            source.Type,
            source.Excluded?.ToArray(),
            source.Unavailable?.ToArray()
        );
        contracts.Add(contract);
        variants.Add(contract);
        return contract;

        static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
            ReferenceEquals(left, right)
            || left is not null
                && right is not null
                && left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private object Failure(int? node, string field, Exception ex)
    {
        var error = (ex.InnerException ?? ex).GetType().Name;
        failures.Add(new(node, field, error, ex.ToString()));
        return new
        {
            status = "incomplete",
            error,
            failure_reference = failures.Count,
        };
    }
}
