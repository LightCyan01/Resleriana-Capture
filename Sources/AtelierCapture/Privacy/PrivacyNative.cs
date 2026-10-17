using System.Globalization;
using System.Text.Json.Serialization;

namespace AtelierCapture;

internal static class PrivacyNative
{
    private static readonly Lazy<Catalog> Fields = new(Load);
    private static readonly Lazy<Dictionary<string, Signature>> Hooks = new(() =>
        Plugin
            .ReadResource<Signature[]>("BattleHooks.json")
            .Concat(Plugin.ReadResource<AtelierSignatures>("Gameplay.AtelierHooks.json").Hooks)
            .ToDictionary(
                value => Key(value.System, value.Boundary, value.Variant),
                StringComparer.Ordinal
            )
    );
    private static readonly HashSet<string> ReferenceContracts = new(StringComparer.Ordinal)
    {
        "native_lifetime_identity_only",
        "receiver_state_not_observed",
        "historical_state_not_recopied",
        "presentation_controller_state_not_observed",
        "presentation_view_state_not_observed",
        "scheduler_callback_state_not_observed",
    };

    internal static IEnumerable<string> InterfaceDeclarations(string concreteType) =>
        Fields
            .Value.InterfaceImplementations.Where(pair => pair.Value.Contains(concreteType))
            .Select(pair => pair.Key);

    internal static object Project(object? value, string system)
    {
        var owner = Text(Read(value, "system_id"));
        owner =
            owner is "Battle" or "Atelier" ? owner
            : system is "Battle" or "Atelier" ? system
            : "Unknown";
        var boundary = Text(Read(value, "boundary_id"));
        var variant = Text(Read(value, "variant_id"));
        if (!Hooks.Value.TryGetValue(Key(owner, boundary, variant), out var hook))
            return new { omitted = "privacy_native_boundary_unqualified", complete = false };
        var catalog = Fields.Value;
        var source = (Read(value, "nodes") as IEnumerable<SnapshotNode> ?? []).ToDictionary(node =>
            node.id
        );
        var pending = new Queue<SnapshotNode>();
        var kept = new HashSet<int>();
        var nodes = new List<object>();
        var protobufFieldsOmitted = false;
        var contracts = new Dictionary<int, object>();
        var gaps = new Dictionary<(int? Node, string Field, string Reason), long>();
        int? currentNode = null;
        var currentField = "$instance";

        object Omit(string reason)
        {
            var key = (currentNode, currentField, reason);
            gaps[key] = checked(gaps.GetValueOrDefault(key) + 1);
            return new { omitted = reason };
        }

        bool IsNodeType(string type) =>
            catalog.Types.ContainsKey(type)
            || type.StartsWith("Blend.ApiSchema.Model.", StringComparison.Ordinal)
            || type.StartsWith("Blend.ApiSchema.Api.", StringComparison.Ordinal)
            || Container(type) is not null
            || TupleField(type, "Item1") is not null
            || TupleField(type, "key") is not null;

        object? Copy(object? input, string type)
        {
            type = Unwrap(type);
            if (input is null)
                return null;
            if (type == "System.String" || type == "System.Char")
                return Omit("privacy_native_text_or_bytes");
            if (input is NativeReference reference)
            {
                if (
                    Numeric(type)
                    || type is "System.Boolean" or "System.Single" or "System.Double"
                    || catalog.Enums.ContainsKey(type)
                    || !ReferenceContracts.Contains(reference.state_contract)
                    || Id(reference.lifetime_id) is null
                )
                    return Omit("privacy_native_reference_unqualified");
                return new NativeReference(
                    IsNodeType(reference.type) || reference.type == "UserDataManager"
                        ? reference.type
                        : type,
                    reference.lifetime_id,
                    reference.state_contract
                );
            }
            if (type == "System.Boolean")
                return input is bool ? input : Omit("privacy_native_value_type_mismatch");
            if (type is "System.Single" or "System.Double")
            {
                var bits = Text(Read(input, "bits"));
                var width = type == "System.Single" ? 8 : 16;
                if (bits is null || bits.Length != width || !bits.All(Uri.IsHexDigit))
                    return Omit("privacy_native_float_unqualified");
                var number = ulong.Parse(
                    bits,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture
                );
                return type == "System.Single"
                    ? Float(BitConverter.UInt32BitsToSingle((uint)number), bits)
                    : Double(BitConverter.UInt64BitsToDouble(number), bits);
            }
            if (Numeric(type))
                return Number(input, type, out var numeric)
                    ? numeric
                    : Omit("privacy_native_value_type_mismatch");
            if (
                Read(input, "underlying_type") is string underlying
                && Read(input, "value") is string enumValue
            )
            {
                var expected = input is SnapshotEnum original
                    ? original.DeclaredType == type
                        ? catalog.Enums.GetValueOrDefault(type) ?? original.underlying_type
                        : null
                    : catalog.Enums.GetValueOrDefault(type);
                if (
                    expected is null
                    || !Numeric(underlying)
                    || underlying is "System.Decimal" or "System.Single" or "System.Double"
                    || !Number(enumValue, underlying, out _)
                    || expected != underlying
                )
                    return Omit("privacy_native_enum_unqualified");
                return new { underlying_type = underlying, value = enumValue };
            }
            if (
                (IsNodeType(type) || catalog.Interfaces.Contains(type))
                && Read(input, "reference") is int id
                && source.TryGetValue(id, out var target)
                && target.PrivacyType is { } targetType
                && IsNodeType(targetType)
                && (targetType == type || target.AssignableTypes?.Contains(type) == true)
            )
            {
                if (kept.Add(id))
                    pending.Enqueue(target);
                return new { reference = id };
            }
            return Omit("privacy_native_value_unqualified");
        }

        var instance = Copy(Read(value, "instance"), hook.Type);
        var inputs = (Read(value, "values") as IEnumerable<object?> ?? []).ToArray();
        var values = inputs
            .Select(
                (input, index) =>
                {
                    currentField =
                        "$argument[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                    return index < hook.Parameters.Length
                        ? Copy(input, hook.Parameters[index])
                        : Omit("privacy_native_argument_unqualified");
                }
            )
            .ToArray();
        currentField = "$result";
        var result = Copy(Read(value, "result"), hook.Returns);
        while (pending.TryDequeue(out var node))
        {
            var type = node.PrivacyType!;
            var output = new Dictionary<string, object?>(StringComparer.Ordinal);
            var element = Container(type);
            catalog.Types.TryGetValue(type, out var fields);
            currentNode = node.id;
            foreach (var field in node.fields)
            {
                var declared = node.Declarations?.GetValueOrDefault(field.Key);
                var expected = fields?.GetValueOrDefault(field.Key) ?? TupleField(type, field.Key);
                if (declared is null || expected is null || declared != expected)
                {
                    currentField = expected is null ? "$unknown" : field.Key;
                    Omit("privacy_native_field_unqualified");
                    continue;
                }
                currentField = field.Key;
                output[field.Key] = Copy(field.Value, expected);
            }
            object?[]? items = null;
            if (node.items is not null)
            {
                currentField = "$items";
                if (element is null || node.ElementType != element)
                    Omit("privacy_native_collection_unqualified");
                else
                    items = node.items.Select(item => Copy(item, element)).ToArray();
            }
            var protobuf = node.FilteredProtobufQualified ? node.filtered_protobuf : null;
            protobufFieldsOmitted |= node.privacy_omissions is { Count: > 0 };
            currentField = "$protobuf";
            var representation =
                protobuf is not null ? "privacy_qualified_protobuf_json"
                : node.representation?.StartsWith("privacy_protobuf", StringComparison.Ordinal)
                == true
                    ? "privacy_protobuf_unavailable"
                : null;
            if (
                representation == "privacy_protobuf_unavailable"
                || node.protobuf is not null
                || node.filtered_protobuf is not null && !node.FilteredProtobufQualified
            )
                Omit("privacy_native_protobuf_unavailable");
            var omittedFields = node
                .fields.Keys.Where(name => !output.ContainsKey(name))
                .Where(name => fields?.ContainsKey(name) == true)
                .ToArray();
            contracts.TryAdd(
                node.contract_id,
                new
                {
                    id = node.contract_id,
                    type,
                    excluded_fields = omittedFields
                        .Select(name => name + ":privacy_mode")
                        .ToArray(),
                    unavailable_fields = node.unavailable_fields is { Count: > 0 }
                        ? new[] { "native_fields_unavailable" }
                        : Array.Empty<string>(),
                }
            );
            if (node.lineage_failures is { Count: > 0 })
            {
                currentField = "$lineage";
                Omit("native_lineage_unverified");
            }
            nodes.Add(
                new
                {
                    node.id,
                    node.contract_id,
                    fields = output,
                    items,
                    filtered_protobuf = protobuf,
                    representation,
                    privacy_omissions = node.privacy_omissions,
                    lifetime_id = Id(node.lifetime_id),
                }
            );
        }
        var failures = (Read(value, "failures") as IEnumerable<SnapshotFailure> ?? [])
            .Select(failure => new
            {
                node = failure.Node is { } node && kept.Contains(node) ? (int?)node : null,
                error = "native_read_failed",
            })
            .ToArray();
        return new
        {
            invocation_id = Id(Read(value, "invocation_id")),
            parent_invocation_id = Id(Read(value, "parent_invocation_id")),
            boundary_id = boundary,
            variant_id = variant,
            system_id = owner,
            phase = Phase(Read(value, "phase")),
            request_id = Id(Read(value, "request_id")),
            attempt_id = Id(Read(value, "attempt_id")),
            response_id = Id(Read(value, "response_id")),
            status = "incomplete",
            complete = false,
            copied_fields_complete = false,
            source_copied_fields_complete = Read(value, "copied_fields_complete") is true,
            source_lineage_failure_count = (
                Read(value, "lineage_failures") as IEnumerable<string> ?? []
            ).Count(),
            source_correlation_unavailable = Read(value, "correlation_gap") is not null,
            privacy_filtered_fields_complete = gaps.Count == 0
                && failures.Length == 0
                && !protobufFieldsOmitted,
            phase_snapshot_available = Read(value, "phase_snapshot_available") is true,
            native_original_threw = Read(value, "native_original_threw") is true,
            observation_consistency = "owner_thread_sequential_reads_stability_unqualified",
            consistency_gap = "native_owner_stability_unqualified",
            observation_gap = Phase(Read(value, "phase")) == "task_return"
                ? "async_operation_completion_edge_unqualified"
                : null,
            correlation_gap = Read(value, "correlation_gap")
                is "native_input_operations_ambiguous"
                    or "no_proven_native_operation"
                ? (string?)Read(value, "correlation_gap")
                : null,
            projection_contract = "privacy_qualified_native_gameplay_v2",
            snapshot_schema = 2,
            snapshot_contracts = contracts
                .OrderBy(item => item.Key)
                .Select(item => item.Value)
                .ToArray(),
            instance,
            values,
            result,
            nodes,
            failures,
            privacy_omissions = gaps.OrderBy(item => item.Key.Node)
                .ThenBy(item => item.Key.Field, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Reason, StringComparer.Ordinal)
                .Select(item => new
                {
                    node = item.Key.Node,
                    field = item.Key.Field,
                    reason = item.Key.Reason,
                    occurrences = item.Value,
                })
                .ToArray(),
            omitted = "privacy_mode",
        };
    }

    private static object Float(float value, string bits) =>
        new { value = float.IsFinite(value) ? (float?)value : null, bits };

    private static object Double(double value, string bits) =>
        new { value = double.IsFinite(value) ? (double?)value : null, bits };

    private static object? Read(object? value, string name) => CapturePrivacy.Read(value, name);

    private static string? Text(object? value) => value as string;

    private static string? Id(object? value) =>
        value is string text
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0
        && number.ToString(CultureInfo.InvariantCulture) == text
            ? text
            : null;

    private static string Key(string? system, string? boundary, string? variant) =>
        system + "\n" + boundary + "\n" + variant;

    private static string? Phase(object? value) =>
        value is "enter" or "exit" or "task_return" or "exception" ? (string)value : null;

    private static string Unwrap(string type)
    {
        if (type.EndsWith('&'))
            type = type[..^1];
        return type.StartsWith("Il2CppSystem.Nullable`1<", StringComparison.Ordinal)
            ? type[24..^1]
            : type;
    }

    private static bool Numeric(string type) =>
        type
            is "System.Byte"
                or "System.SByte"
                or "System.Int16"
                or "System.UInt16"
                or "System.Int32"
                or "System.UInt32"
                or "System.Int64"
                or "System.UInt64"
                or "System.Decimal";

    private static bool Number(object value, string type, out object? result)
    {
        result = null;
        var text =
            value as string
            ?? (
                value is IFormattable number
                    ? number.ToString(null, CultureInfo.InvariantCulture)
                    : null
            );
        if (text is null || text.Length == 0 || text.Length > 64 || text.Any(char.IsWhiteSpace))
            return false;
        if (type == "System.Decimal" && value is decimal)
        {
            result = value;
            return true;
        }
        if (
            !decimal.TryParse(
                text,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var integer
            )
            || decimal.Truncate(integer) != integer
        )
            return false;
        var valid = type switch
        {
            "System.Byte" => integer >= byte.MinValue && integer <= byte.MaxValue,
            "System.SByte" => integer >= sbyte.MinValue && integer <= sbyte.MaxValue,
            "System.Int16" => integer >= short.MinValue && integer <= short.MaxValue,
            "System.UInt16" => integer >= ushort.MinValue && integer <= ushort.MaxValue,
            "System.Int32" => integer >= int.MinValue && integer <= int.MaxValue,
            "System.UInt32" => integer >= uint.MinValue && integer <= uint.MaxValue,
            "System.Int64" => integer >= long.MinValue && integer <= long.MaxValue,
            "System.UInt64" => integer >= ulong.MinValue && integer <= ulong.MaxValue,
            _ => false,
        };
        if (!valid)
            return false;
        result =
            type is "System.Int64" or "System.UInt64" ? text
            : value is string ? integer
            : value;
        return true;
    }

    private static string? Container(string type)
    {
        var split = Generic(type);
        if (split is null)
            return null;
        var (name, arguments) = split.Value;
        if (
            arguments.Any(argument =>
                argument is "System.String" or "System.Byte" or "System.SByte" or "System.Char"
            )
        )
            return null;
        if (
            arguments.Length == 1
            && (
                name.StartsWith("Il2CppSystem.Collections.Generic.", StringComparison.Ordinal)
                || name
                    is "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1"
                        or "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1"
            )
        )
            return arguments[0];
        return
            arguments.Length == 2
            && name.Contains("Dictionary", StringComparison.Ordinal)
            && arguments[0]
                is "System.Int32"
                    or "System.Int64"
                    or "System.UInt32"
                    or "System.UInt64"
            ? "Il2CppSystem.Collections.Generic.KeyValuePair`2<" + string.Join(",", arguments) + ">"
            : null;
    }

    private static string? TupleField(string type, string name)
    {
        var split = Generic(type);
        if (split is not { } tuple)
            return null;
        if (tuple.Name == "Il2CppSystem.Collections.Generic.KeyValuePair`2")
            return name == "key" ? tuple.Arguments[0]
                : name == "value" ? tuple.Arguments[1]
                : null;
        if (
            !tuple.Name.StartsWith("Il2CppSystem.ValueTuple`", StringComparison.Ordinal)
            && !tuple.Name.StartsWith("Il2CppSystem.Tuple`", StringComparison.Ordinal)
        )
            return null;
        var indexText =
            name.StartsWith("m_Item", StringComparison.Ordinal) ? name[6..]
            : name.StartsWith("Item", StringComparison.Ordinal) ? name[4..]
            : "";
        return
            int.TryParse(indexText, out var index) && index >= 1 && index <= tuple.Arguments.Length
            ? tuple.Arguments[index - 1]
            : null;
    }

    private static (string Name, string[] Arguments)? Generic(string type)
    {
        var start = type.IndexOf('<');
        if (start < 0 || !type.EndsWith('>'))
            return null;
        var arguments = new List<string>();
        var depth = 0;
        var from = start + 1;
        for (var index = from; index < type.Length - 1; index++)
        {
            if (type[index] == '<')
                depth++;
            else if (type[index] == '>')
                depth--;
            else if (type[index] == ',' && depth == 0)
            {
                arguments.Add(type[from..index]);
                from = index + 1;
            }
        }
        arguments.Add(type[from..^1]);
        return (type[..start], arguments.ToArray());
    }

    private static Catalog Load()
    {
        var catalog = Plugin.ReadResource<Catalog>("NativePrivacyFields.json");
        if (
            catalog.Schema != 1
            || catalog.ClientVersion != "2.16.0"
            || catalog.GameassemblySha256
                != "35868ee52cb199b018e2ced962dd233a9f4d200a7e5d8e9903e7f4607f21f5ea"
            || catalog.MetadataSha256
                != "af138395b956bc835f99b2fd140a4c5015781e56c9cab3052bdb7e69065cc9b0"
        )
            throw new InvalidDataException("Native privacy field declarations are unavailable.");
        return catalog;
    }

    private sealed record Signature(
        string Type,
        string[] Parameters,
        string Returns,
        string System = "Battle",
        string Boundary = "",
        string Variant = "entry_exit"
    );

    private sealed record AtelierSignatures(Signature[] Hooks);

    private sealed record Catalog(
        int Schema,
        [property: JsonPropertyName("client_version")] string ClientVersion,
        [property: JsonPropertyName("gameassembly_sha256")] string GameassemblySha256,
        [property: JsonPropertyName("metadata_sha256")] string MetadataSha256,
        Dictionary<string, Dictionary<string, string>> Types,
        Dictionary<string, string> Enums,
        string[] Interfaces,
        [property: JsonPropertyName("interface_implementations")]
            Dictionary<string, string[]> InterfaceImplementations
    );
}
