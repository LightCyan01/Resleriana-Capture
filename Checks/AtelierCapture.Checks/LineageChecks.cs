using System.Reflection;
using System.Reflection.Emit;

namespace AtelierCapture.Checks;

internal static class LineageChecks
{
    internal static void Run()
    {
        var module = AssemblyBuilder
            .DefineDynamicAssembly(
                new AssemblyName("SyntheticLineageShapes"),
                AssemblyBuilderAccess.Run
            )
            .DefineDynamicModule("Shapes");
        Type Message(string name) =>
            module.DefineType(name, TypeAttributes.Public | TypeAttributes.Class).CreateType()!;
        var responseType = Message("Blend.ApiSchema.Api.BattleFinishResponse");
        var childType = Message("Blend.ApiSchema.Model.BattleResult");
        var enumBuilder = module.DefineEnum(
            "Blend.ApiSchema.Api.BattleFinishResponse+ResultOneofCase",
            TypeAttributes.Public,
            typeof(int)
        );
        enumBuilder.DefineLiteral("None", 0);
        enumBuilder.DefineLiteral("Win", 1);
        var enumType = enumBuilder.CreateTypeInfo()!.AsType();
        var repeatedBuilder = module.DefineType(
            "Google.Protobuf.Collections.RepeatedField`1",
            TypeAttributes.Public
        );
        repeatedBuilder.DefineGenericParameters("T");
        var repeatedDefinition = repeatedBuilder.CreateType()!;
        var repeatedType = repeatedDefinition.MakeGenericType(childType);
        Require(
            !NativeProtobuf.IsMessage(enumType)
                && !NativeLineage.IsMessageChild(enumType, false)
                && !NativeLineage.IsMessageChild(enumType, true)
                && NativeProtobuf.IsMessage(responseType)
                && NativeProtobuf.IsMessageCollection(repeatedType)
                && !NativeProtobuf.IsMessageCollection(repeatedDefinition.MakeGenericType(enumType))
                && NativeLineage.IsMessageChild(childType, true)
                && NativeLineage.IsMessageChild(repeatedType, true),
            "A nested oneof enum became a message, or a repeated message lost its qualified child type."
        );
        foreach (
            var scalar in new object[]
            {
                false,
                (byte)7,
                (sbyte)-1,
                (short)-2,
                (ushort)3,
                -4,
                5U,
                -6L,
                7UL,
                '\0',
                0.0F,
                double.NaN,
                "oneof scalar",
                8M,
                new byte[] { 8, 1 },
                Enum.ToObject(enumType, 0),
                Enum.ToObject(enumType, 1),
            }
        )
            Require(
                !NativeLineage.IsMessageChild(scalar.GetType(), false),
                "A known oneof scalar attempted to acquire native object identity."
            );
        foreach (var native in new[] { false, true })
            try
            {
                NativeLineage.IsMessageChild(typeof(object), native);
                throw new InvalidOperationException("Unknown child type was silently omitted.");
            }
            catch (InvalidDataException) { }
        foreach (
            var name in new[]
            {
                "Boolean",
                "Byte",
                "SByte",
                "Int16",
                "UInt16",
                "Int32",
                "UInt32",
                "Int64",
                "UInt64",
                "Single",
                "Double",
                "Char",
                "Decimal",
                "String",
            }
        )
        {
            var boxedType = Message("Il2CppSystem." + name);
            Require(
                NativeLineage.IsScalarNativeClass("System." + name)
                    && NativeLineage.IsScalarNativeClass(boxedType.FullName)
                    && !NativeLineage.IsMessageChild(boxedType, true),
                "A known native primitive box became a message child."
            );
            try
            {
                NativeLineage.IsMessageChild(boxedType, false);
                throw new InvalidOperationException(
                    "A managed primitive-name lookalike became native proof."
                );
            }
            catch (InvalidDataException) { }
        }
        foreach (var name in new[] { "Il2CppSystem.Enum", "Google.Protobuf.ByteString" })
            try
            {
                NativeLineage.IsMessageChild(Message(name), true);
                throw new InvalidOperationException(
                    "An unqualified native boxed/blob type was silently skipped."
                );
            }
            catch (InvalidDataException) { }
        Require(
            !NativeLineage.IsScalarNativeClass(null)
                && !NativeLineage.IsScalarNativeClass("System.SomeMessage")
                && !NativeLineage.IsScalarNativeClass("System.Int32Message")
                && !NativeLineage.IsScalarNativeClass("Il2CppSystem.Int32[]")
                && !NativeLineage.IsScalarNativeClass("System.Enum")
                && !NativeLineage.IsScalarNativeClass("Google.Protobuf.ByteString"),
            "A prefix, array, generic enum or blob name became an unqualified scalar exemption."
        );
        try
        {
            NativeLineage.IsMessageChild(childType, false);
            throw new InvalidOperationException("A managed lookalike acquired native provenance.");
        }
        catch (InvalidDataException) { }
        Require(
            NativeLineage.IsStoredChildField("Il2CppSystem.Object")
                && NativeLineage.IsStoredChildField("Google.Protobuf.IMessage")
                && NativeLineage.IsStoredChildField(enumType.FullName)
                && NativeLineage.IsStoredChildField(null)
                && !NativeLineage.IsStoredChildField("System.Int32")
                && !NativeLineage.IsStoredChildField("UnityEngine.Material"),
            "Generated object/interface oneof slots were skipped or unrelated render fields entered lineage."
        );

        var response = Activator.CreateInstance(responseType)!;
        var first = Activator.CreateInstance(childType)!;
        var second = Activator.CreateInstance(childType)!;
        var repeated = Activator.CreateInstance(repeatedType)!;
        var nodes = new Dictionary<object, Dictionary<string, (string? Type, Func<object?> Read)>>
        {
            [response] = new()
            {
                ["result_"] = ("Il2CppSystem.Object", () => first),
                ["resultCase_"] = (enumType.FullName, () => Enum.ToObject(enumType, 1)),
                ["untyped_scalar"] = (null, () => 17),
                ["repeated_"] = (repeatedType.FullName, () => repeated),
            },
            [first] = new(),
            [second] = new(),
            [repeated] = new() { ["items[0]"] = (childType.FullName, () => second) },
        };
        var context = new NetworkContext(
            "1",
            "1",
            "2",
            "/battle/finish",
            "Battle",
            false,
            "3",
            "original_parse"
        );
        var bindings = new Dictionary<object, ContextBinding> { [response] = new() };
        bindings[response].Bind(context);
        ContextBinding Binding(object value)
        {
            if (!bindings.TryGetValue(value, out var binding))
                bindings[value] = binding = new();
            return binding;
        }
        string[] Walk() =>
            NativeLineage.BindChildren(
                response,
                value => value,
                parent =>
                    nodes[parent]
                        .Where(p => NativeLineage.IsStoredChildField(p.Value.Type))
                        .Select(p => new SnapshotMember(
                            p.Key,
                            () =>
                            {
                                var child = p.Value.Read();
                                return child is null ? null
                                    : NativeLineage.IsMessageChild(
                                        child.GetType(),
                                        nodes.ContainsKey(child)
                                    )
                                        ? child
                                    : null;
                            }
                        )),
                (parent, child, edge) =>
                    NativeLineage.Transfer(
                        parent,
                        child,
                        edge,
                        value => Binding(value).State,
                        (value, proof, source) => Binding(value).Bind(proof, source),
                        (value, origin, source) => Binding(value).Origin.Bind(origin, source),
                        value => Binding(value).InvalidateOperation()
                    ),
                value => Binding(value).State
            );
        Require(
            Walk().Length == 0
                && Binding(first).Current == context
                && Binding(second).Current == context
                && bindings.Count == 4
                && NativeLineage.ParsedRootGap(context, Binding(response).State) is null,
            "Stored object oneof/repeated message lineage failed or bound its scalar discriminator."
        );
        nodes[response]["result_"] = ("Il2CppSystem.Object", () => null);
        nodes[response]["resultCase_"] = (enumType.FullName, () => Enum.ToObject(enumType, 0));
        Require(
            Walk().Length == 0 && bindings.Count == 4,
            "Fresh null/discriminator changes became false child failures or invented native lifetimes."
        );
        nodes[response]["result_"] = ("Il2CppSystem.Object", () => new object());
        Require(
            Walk()
                .Single()
                .Contains(
                    "Native protobuf lineage child type is unqualified",
                    StringComparison.Ordinal
                ),
            "An unknown object oneof disappeared as a successful null child."
        );
        var error = new MissingMemberException(
            "actual_generated_reader",
            new InvalidDataException("inner")
        );
        nodes[response]["result_"] = ("Il2CppSystem.Object", () => throw error);
        Require(
            Walk().Single() == "result_:" + error,
            "A genuine oneof getter fault lost its full exception or became a scalar exemption."
        );
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
