using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AtelierCapture;

internal static class PrivacyPayload
{
    internal sealed record Omission(
        string message_type,
        string field,
        string reason,
        long occurrences
    );

    private static readonly Lazy<Catalog> Fields = new(Load);
    private static readonly Dictionary<string, string> Wrappers = new(StringComparer.Ordinal)
    {
        ["google.protobuf.BoolValue"] = "bool",
        ["google.protobuf.Int32Value"] = "int32",
        ["google.protobuf.UInt32Value"] = "uint32",
        ["google.protobuf.Int64Value"] = "int64",
        ["google.protobuf.UInt64Value"] = "uint64",
        ["google.protobuf.FloatValue"] = "float",
        ["google.protobuf.DoubleValue"] = "double",
        ["google.protobuf.StringValue"] = "string",
    };
    private static readonly Regex Timestamp = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.[0-9]{1,9})?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    );

    internal static JsonNode? Project(string nativeType, JsonNode? payload, out bool omitted) =>
        Project(nativeType, payload, out omitted, out _);

    internal static JsonNode? Project(
        string nativeType,
        JsonNode? payload,
        out bool omitted,
        out IReadOnlyList<Omission> omissions
    )
    {
        var catalog = Fields.Value;
        var excluded = false;
        var missing = new Dictionary<(string Owner, string Field, string Reason), long>();
        JsonNode? result = null;
        var pending = new Stack<Action>();
        Schedule(payload, Normalize(nativeType), value => result = value, static () => { });
        while (pending.TryPop(out var next))
            next();
        omitted = excluded;
        omissions = missing
            .Select(row => new Omission(row.Key.Owner, row.Key.Field, row.Key.Reason, row.Value))
            .ToArray();
        return result;

        void Schedule(
            JsonNode? input,
            string type,
            Action<JsonNode?> save,
            Action reject,
            string owner = "$root",
            string field = "$value"
        ) => pending.Push(() => Visit(input, type, save, reject, owner, field));

        void Missing(string owner, string field, string reason)
        {
            excluded = true;
            var key = (owner, field, reason);
            missing[key] = checked(missing.GetValueOrDefault(key) + 1);
        }

        void Reject(Action reject, string owner, string field, string reason)
        {
            Missing(owner, field, reason);
            reject();
        }

        bool Known(string type)
        {
            while (type.StartsWith("[]", StringComparison.Ordinal))
                type = type[2..];
            return catalog.messages.ContainsKey(type)
                || Wrappers.ContainsKey(type)
                || type
                    is "bool"
                        or "int32"
                        or "uint32"
                        or "int64"
                        or "uint64"
                        or "float"
                        or "double"
                        or "string"
                || type == "google.protobuf.Timestamp"
                || type.StartsWith("enum:", StringComparison.Ordinal)
                    && catalog.enums.ContainsKey(type[5..]);
        }

        void Visit(
            JsonNode? input,
            string type,
            Action<JsonNode?> save,
            Action reject,
            string owner,
            string field
        )
        {
            if (!Known(type))
            {
                Reject(reject, owner, field, "unqualified_message_or_type");
                return;
            }
            if (input is null)
            {
                save(null);
                return;
            }
            if (type.StartsWith("[]", StringComparison.Ordinal))
            {
                if (input is not JsonArray array)
                {
                    Reject(reject, owner, field, "invalid_array_representation");
                    return;
                }
                var output = new JsonArray();
                for (var index = 0; index < array.Count; index++)
                    output.Add((JsonNode?)null);
                var invalid = false;
                pending.Push(() =>
                {
                    // A rejected element removes its array rather than inventing a null or changing order.
                    if (invalid)
                        Reject(reject, owner, field, "array_contains_omitted_element");
                    else
                        save(output);
                });
                for (var index = array.Count - 1; index >= 0; index--)
                {
                    var position = index;
                    Schedule(
                        array[index],
                        type[2..],
                        value => output[position] = value,
                        () => invalid = true,
                        owner,
                        field
                    );
                }
                return;
            }
            if (Wrappers.TryGetValue(type, out var scalar))
                type = scalar;
            if (catalog.messages.TryGetValue(type, out var fields))
            {
                if (input is not JsonObject source)
                {
                    Reject(reject, owner, field, "invalid_message_representation");
                    return;
                }
                var output = new JsonObject();
                pending.Push(() => save(output));
                foreach (var member in source)
                {
                    if (!fields.TryGetValue(member.Key, out var childType))
                    {
                        var known = catalog.Redactions.TryGetValue(
                            (type, member.Key),
                            out var reason
                        );
                        Missing(
                            type,
                            known ? member.Key : "$unknown",
                            known ? reason! : "unqualified_field_name"
                        );
                        continue;
                    }
                    var name = member.Key;
                    Schedule(
                        member.Value,
                        childType,
                        value => output.Add(name, value),
                        static () => { },
                        type,
                        name
                    );
                }
                return;
            }
            if (input is JsonValue value && Scalar(value, type, catalog))
                save(value.DeepClone());
            else
                Reject(reject, owner, field, "invalid_or_unsupported_scalar");
        }
    }

    private static bool Scalar(JsonValue value, string type, Catalog catalog)
    {
        var kind = value.GetValueKind();
        if (type == "string")
            return kind == JsonValueKind.String;
        if (type == "bool")
            return kind is JsonValueKind.True or JsonValueKind.False;
        if (type == "google.protobuf.Timestamp")
        {
            if (kind != JsonValueKind.String)
                return false;
            var text = value.GetValue<string>();
            if (text.Length > 35)
                return false;
            var match = Timestamp.Match(text);
            return match.Success
                && DateTimeOffset.TryParse(
                    match.Groups[1].Value + match.Groups[2].Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _
                );
        }
        if (type.StartsWith("enum:", StringComparison.Ordinal))
        {
            var values = catalog.enums[type[5..]];
            if (kind == JsonValueKind.String)
                return values.ContainsKey(value.GetValue<string>());
            return kind == JsonValueKind.Number
                && int.TryParse(
                    value.ToJsonString(),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var number
                );
        }
        if (kind is not (JsonValueKind.Number or JsonValueKind.String))
            return false;
        var numeric =
            kind == JsonValueKind.Number ? value.ToJsonString() : value.GetValue<string>();
        if (numeric.Length > 64 || numeric.Length == 0 || numeric.Any(char.IsWhiteSpace))
            return false;
        if (type is "float" or "double")
        {
            if (kind == JsonValueKind.String)
                return numeric is "NaN" or "Infinity" or "-Infinity";
            return type == "float"
                ? float.TryParse(
                    numeric,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var single
                ) && float.IsFinite(single)
                : double.TryParse(
                    numeric,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var real
                ) && double.IsFinite(real);
        }
        if (kind == JsonValueKind.String && type is not ("int64" or "uint64"))
            return false;
        return type switch
        {
            "int32" => int.TryParse(
                numeric,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _
            ),
            "uint32" => uint.TryParse(
                numeric,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _
            ),
            "int64" => long.TryParse(
                numeric,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _
            ),
            "uint64" => ulong.TryParse(
                numeric,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _
            ),
            _ => false,
        };
    }

    private static string Normalize(string name) =>
        name.Replace("Blend.ApiSchema.Api.", "blend.api.", StringComparison.Ordinal)
            .Replace("Blend.ApiSchema.Model.", "blend.model.", StringComparison.Ordinal)
            .Replace(
                "Google.Protobuf.WellKnownTypes.",
                "google.protobuf.",
                StringComparison.Ordinal
            )
            .Replace('+', '.');

    private static Catalog Load()
    {
        var catalog = Plugin.ReadResource<Catalog>("PrivacyFields.json");
        if (
            catalog.schema != 1
            || catalog.descriptor_sha256
                != "96264797a38e2643be227ebe68e625fcf5e01dd4ff5ef01d2e08a1771a4ddbff"
        )
            throw new InvalidDataException("Privacy gameplay field catalog is unavailable.");
        foreach (var fields in catalog.messages.Values)
        foreach (var field in fields.ToArray())
        {
            var parts = field.Key.Split('_');
            var jsonName =
                parts[0]
                + string.Concat(
                    parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..])
                );
            if (jsonName != field.Key)
                fields.Add(jsonName, field.Value);
        }
        foreach (var excluded in catalog.excluded_fields)
        {
            var split = excluded.LastIndexOf('.');
            if (split < 1)
                continue;
            var owner = excluded[..split];
            var field = excluded[(split + 1)..];
            var reason = catalog.redaction_reasons?.GetValueOrDefault(excluded) ?? "personal_data";
            catalog.Redactions.Add((owner, field), reason);
            var parts = field.Split('_');
            catalog.Redactions.TryAdd(
                (
                    owner,
                    parts[0]
                        + string.Concat(
                            parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..])
                        )
                ),
                reason
            );
        }
        return catalog;
    }

    private sealed record Catalog(
        int schema,
        string descriptor_sha256,
        string[] excluded_fields,
        Dictionary<string, Dictionary<string, string>> messages,
        Dictionary<string, Dictionary<string, int>> enums,
        Dictionary<string, string>? redaction_reasons = null
    )
    {
        internal Dictionary<(string Owner, string Field), string> Redactions { get; } = [];
    }
}
