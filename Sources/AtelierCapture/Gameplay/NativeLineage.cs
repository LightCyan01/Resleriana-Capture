namespace AtelierCapture;

internal static class NativeLineage
{
    internal static bool IsScalarNativeClass(string? name) =>
        name
            is "System.Boolean"
                or "System.Byte"
                or "System.SByte"
                or "System.Int16"
                or "System.UInt16"
                or "System.Int32"
                or "System.UInt32"
                or "System.Int64"
                or "System.UInt64"
                or "System.Single"
                or "System.Double"
                or "System.Char"
                or "System.Decimal"
                or "System.String"
                or "Il2CppSystem.Boolean"
                or "Il2CppSystem.Byte"
                or "Il2CppSystem.SByte"
                or "Il2CppSystem.Int16"
                or "Il2CppSystem.UInt16"
                or "Il2CppSystem.Int32"
                or "Il2CppSystem.UInt32"
                or "Il2CppSystem.Int64"
                or "Il2CppSystem.UInt64"
                or "Il2CppSystem.Single"
                or "Il2CppSystem.Double"
                or "Il2CppSystem.Char"
                or "Il2CppSystem.Decimal"
                or "Il2CppSystem.String";

    internal static bool IsStoredChildField(string? type) =>
        type is null or "Il2CppSystem.Object" or "Google.Protobuf.IMessage"
        || type.StartsWith("Blend.ApiSchema.Model.", StringComparison.Ordinal)
        || type.StartsWith("Blend.ApiSchema.Api.", StringComparison.Ordinal)
        || type.StartsWith("Google.Protobuf.WellKnownTypes.", StringComparison.Ordinal)
        || type.StartsWith("Google.Protobuf.Collections.RepeatedField", StringComparison.Ordinal)
            && (
                type.Contains("Blend.ApiSchema.Model.", StringComparison.Ordinal)
                || type.Contains("Blend.ApiSchema.Api.", StringComparison.Ordinal)
                || type.Contains("Google.Protobuf.WellKnownTypes.", StringComparison.Ordinal)
            );

    internal static bool IsMessageChild(Type type, bool nativeObject)
    {
        if (
            type.IsEnum
            || type.IsPrimitive
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(byte[])
        )
            return false;
        if (nativeObject && IsScalarNativeClass(type.FullName))
            return false;
        if (
            nativeObject
            && (NativeProtobuf.IsMessage(type) || NativeProtobuf.IsMessageCollection(type))
        )
            return true;
        throw new InvalidDataException(
            "Native protobuf lineage child type is unqualified: " + type.FullName
        );
    }

    internal static string? ParsedRootGap(NetworkContext? expected, NativeBindingState proof)
    {
        if (expected is null)
            return "native_parse_input_unbound";
        if (proof.OperationAmbiguous)
            return "native_parse_root_operation_ambiguous";
        if (proof.EnvironmentAmbiguous)
            return "native_parse_root_origin_ambiguous";
        if (proof.Context is not { } effective)
            return "native_parse_root_context_unavailable";
        if (ContextBinding.Conflicts(effective, expected))
            return "native_parse_root_context_mismatch";
        return !proof.EnvironmentHasEvidence || proof.Environment is null
            ? "native_parse_root_origin_unknown"
            : null;
    }

    internal static string? Transfer(
        object parent,
        object child,
        string edge,
        Func<object, NativeBindingState> bindingState,
        Action<object, NetworkContext, string> bindContext,
        Action<object, bool?, string> bindOrigin,
        Action<object> invalidateOperation
    )
    {
        var proof = bindingState(parent);
        if (proof.Context is { } proven)
        {
            bindContext(child, proven, edge);
            var childProof = bindingState(child);
            if (childProof.OperationAmbiguous)
                return edge + ":native_child_operation_ambiguous";
            if (childProof.EnvironmentAmbiguous)
                return edge + ":native_child_origin_ambiguous";
            return proven.IsPrivate is null ? edge + ":native_parent_origin_unknown" : null;
        }
        if (proof.EnvironmentHasEvidence)
            bindOrigin(child, proof.Environment, edge);
        if (proof.OperationAmbiguous)
        {
            invalidateOperation(child);
            return edge + ":native_parent_operation_ambiguous";
        }
        return proof.Environment is null
            ? edge + ":native_parent_origin_unknown_or_ambiguous"
            : null;
    }

    internal static string[] BindChildren(
        object root,
        Func<object, object> identity,
        Func<object, IEnumerable<SnapshotMember>> children,
        Action<object, object, string> bind,
        Func<object, NativeBindingState> bindingState
    )
    {
        var pending = new Queue<object>();
        var seen = new Dictionary<object, NativeBindingState>();
        var failures = new List<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var parent))
        {
            try
            {
                var key = identity(parent);
                var proof = bindingState(parent);
                if (seen.TryGetValue(key, out var previous) && previous == proof)
                    continue;
                seen[key] = proof;
                foreach (var field in children(parent))
                {
                    try
                    {
                        if (field.Read() is not { } child)
                            continue;
                        bind(parent, child, field.Name);
                        pending.Enqueue(child);
                    }
                    catch (Exception error)
                    {
                        failures.Add(field.Name + ":" + error);
                    }
                }
            }
            catch (Exception error)
            {
                failures.Add("$lineage_children:" + error);
            }
        }
        return failures.ToArray();
    }
}
