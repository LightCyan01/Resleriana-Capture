using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace AtelierCapture;

internal sealed class Snapshot
{
    private static readonly ConcurrentDictionary<Type, Field[]> Fields = new();
    private static readonly ConcurrentDictionary<Type, Projection> Projections = new();
    private static readonly ConcurrentDictionary<Type, string> ChildEdges = new();
    private static readonly ConcurrentDictionary<
        Type,
        IReadOnlyDictionary<string, string>
    > PrivacyDeclarations = new();
    private static readonly ConcurrentDictionary<Type, string[]> PrivacyAssignments = new();
    private static readonly ConcurrentDictionary<
        Type,
        Func<object, IEnumerable<object?>>
    > Collections = new();
    private readonly DetachedSnapshot graph;
    private readonly bool privacy;

    internal Snapshot(bool privacy = false)
    {
        this.privacy = privacy;
        graph = new(Describe, Identity);
    }

    internal IReadOnlyList<SnapshotNode> Nodes => graph.Nodes;
    internal IReadOnlyList<SnapshotFailure> Failures => graph.Failures;
    internal bool Complete => graph.Complete;
    internal long RetainedBytes => graph.RetainedBytes;
    internal IReadOnlyList<SnapshotContract> Contracts => graph.Contracts;

    internal static void Initialize()
    {
        foreach (var name in Plugin.ReadResource<string[]>("SnapshotTypes.json"))
            Fields.GetOrAdd(NativeContract.ResolveType(name), NativeFields);
    }

    internal static void CheckReaders()
    {
        var list = new Il2CppSystem.Collections.Generic.List<int>();
        list.Add(17);
        list.Add(29);
        if (!Equals(ReadStoredChild(list, "_size"), 2))
            throw new InvalidDataException("Stored native list count reader failed");
        var view = new Il2CppSystem.Collections.Generic.IList<int>(list.Pointer);
        var enumerable = new Il2CppSystem.Collections.Generic.IEnumerable<int>(list.Pointer);
        foreach (var source in new Il2CppObjectBase[] { list, view, enumerable })
        {
            var values = Collections
                .GetOrAdd(source.GetType(), CollectionReader)(source)
                .Take(3)
                .Cast<int>();
            if (!values.SequenceEqual(new[] { 17, 29 }))
                throw new InvalidDataException("Native list reader failed");
        }
        list.Clear();
        if (!Equals(ReadStoredChild(list, "_size"), 0))
            throw new InvalidDataException("Stored empty native list count reader failed");
        if (Collections.GetOrAdd(enumerable.GetType(), CollectionReader)(enumerable).Any())
            throw new InvalidDataException("Empty native enumerable reader failed");
        var map = new Il2CppSystem.Collections.Generic.Dictionary<int, int>();
        map.Add(7, 11);
        map.Add(13, 19);
        map.Add(23, 31);
        map.Remove(13);
        var pairs = Collections.GetOrAdd(map.GetType(), CollectionReader)(map).Take(3).ToArray();
        if (pairs.Length != 2)
            throw new InvalidDataException("Native dictionary reader did not terminate");
        var fields = Fields.GetOrAdd(pairs[0]!.GetType(), NativeFields);
        var key = fields.Single(f => f.Name == "key").Get!;
        var value = fields.Single(f => f.Name == "value").Get!;
        var actual = pairs.Select(p => ((int)key(p!)!, (int)value(p!)!)).OrderBy(p => p.Item1);
        if (!actual.SequenceEqual(new[] { (7, 11), (23, 31) }))
            throw new InvalidDataException("Native dictionary values changed");

        var optionalInt = NewNullable<int>();
        var optionalLong = NewNullable<long>();
        if (
            new Snapshot().Read(optionalInt) is not null
            || new Snapshot().Read(optionalLong) is not null
        )
            throw new InvalidDataException("Empty nullable reader failed");
        optionalInt.GetType().GetProperty("hasValue")!.SetValue(optionalInt, true);
        optionalLong.GetType().GetProperty("hasValue")!.SetValue(optionalLong, true);
        optionalInt.value = -17;
        optionalLong.value = 9007199254741007L;
        if (
            !Equals(new Snapshot().Read(optionalInt), -17)
            || !Equals(new Snapshot().Read(optionalLong), "9007199254741007")
        )
            throw new InvalidDataException("Nullable scalar reader failed");
        if (
            !Equals(
                new Snapshot().Read(
                    NewNullable<long>(IL2CPP.il2cpp_object_unbox(optionalLong.Pointer))
                ),
                "9007199254741007"
            )
        )
            throw new InvalidDataException("Nullable argument copy failed");

        var tuple = new Il2CppSystem.Tuple<int, bool>(23, true);
        var tupleFields = Fields.GetOrAdd(tuple.GetType(), NativeFields);
        if (
            !Equals(tupleFields.Single(f => f.Name == "m_Item1").Get!(tuple), 23)
            || !Equals(tupleFields.Single(f => f.Name == "m_Item2").Get!(tuple), true)
        )
            throw new InvalidDataException("Native tuple reader failed");
        var references = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Tuple<int, bool>>();
        references.Add(tuple);
        var referenceView = new Il2CppSystem.Collections.Generic.IEnumerable<Il2CppSystem.Tuple<
            int,
            bool
        >>(references.Pointer);
        var referenceItems = Collections
            .GetOrAdd(referenceView.GetType(), CollectionReader)(referenceView)
            .Take(2)
            .Cast<Il2CppSystem.Tuple<int, bool>>()
            .ToArray();
        if (referenceItems.Length != 1 || referenceItems[0].Pointer != tuple.Pointer)
            throw new InvalidDataException("Native enumerable reference changed");
        var nestedType = NativeContract
            .ResolveType("Il2CppSystem.ValueTuple`2")
            .MakeGenericType(typeof(int), typeof(Il2CppSystem.Nullable<int>));
        var nested = (Il2CppObjectBase)Activator.CreateInstance(nestedType)!;
        nestedType.GetProperty("Item1")!.SetValue(nested, 31);
        var nestedField =
            Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
                nested.GetType().GetProperty("Item2")!.GetMethod!
            )!;
        var optionalField =
            Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
                optionalInt.GetType().GetProperty("value")!.GetMethod!
            )!;
        var nestedPointer =
            nested.Pointer
            + (int)IL2CPP.il2cpp_field_get_offset((IntPtr)nestedField.GetValue(null)!);
        var nestedRead = Fields
            .GetOrAdd(nested.GetType(), NativeFields)
            .Single(f => f.Name == "Item2")
            .Get!;
        if (nestedRead(nested) is not null)
            throw new InvalidDataException("Empty nested nullable reader failed");
        Marshal.WriteByte(nestedPointer, 1);
        Marshal.WriteInt32(
            nestedPointer,
            (int)IL2CPP.il2cpp_field_get_offset((IntPtr)optionalField.GetValue(null)!)
                - 2 * IntPtr.Size,
            47
        );
        if (!Equals(nestedRead(nested), 47))
            throw new InvalidDataException("Nested nullable reader failed");
        foreach (
            var name in new[]
            {
                "UnityEngine.Vector3",
                "UnityEngine.Quaternion",
                "Il2CppSystem.DateTime",
            }
        )
        {
            var type = NativeContract.ResolveType(name);
            var sample = Activator.CreateInstance(type)!;
            var fieldsForValue = type.GetFields(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly
            );
            foreach (var field in fieldsForValue)
                field.SetValue(
                    sample,
                    name == "Il2CppSystem.DateTime"
                        ? (object)0xC000000000000029UL
                        : BitConverter.UInt32BitsToSingle(
                            field.Name == "x" ? 0x80000000u
                            : field.Name == "y" ? 0x7FC00029u
                            : 0x3F800000u
                        )
                );
            var snapshot = new Snapshot();
            snapshot.Read(sample);
            var node = snapshot.Nodes.Single();
            if (!snapshot.Complete || node.type != name || node.lifetime_id is not null)
                throw new InvalidDataException("Stored native value reader failed: " + name);
            if (name == "Il2CppSystem.DateTime")
            {
                if (!Equals(node.fields["_dateData"], "13835058055282163753"))
                    throw new InvalidDataException("Native DateTime flags changed");
                continue;
            }
            using var encoded = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node.fields)
            );
            foreach (var field in fieldsForValue)
                if (
                    encoded.RootElement.GetProperty(field.Name).GetProperty("bits").GetString()
                    != (
                        field.Name == "x" ? "80000000"
                        : field.Name == "y" ? "7FC00029"
                        : "3F800000"
                    )
                )
                    throw new InvalidDataException(
                        "Native vector/quaternion bits changed: " + name + "." + field.Name
                    );
        }
        CheckQueryAndEncryptedReaders();
    }

    private static void CheckQueryAndEncryptedReaders()
    {
        var queryOwner = NativeContract.ResolveType("BattleStateVariables");
        foreach (
            var expected in new[]
            {
                ("m_actionInfoList", 0x198),
                ("m_actionSetupInfoList", 0x1A0),
                ("_MHHGMPCBOKI_k__BackingField", 0x1BE),
                ("_ENPKCFFLCFM_k__BackingField", 0x168),
            }
        )
        {
            var property =
                queryOwner.GetProperty(expected.Item1)
                ?? throw new MissingMemberException(expected.Item1);
            var metadata =
                Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
                    property.GetMethod!
                )
                ?? throw new MissingMemberException(
                    "Required client-status field metadata: " + expected.Item1
                );
            if (IL2CPP.il2cpp_field_get_offset((IntPtr)metadata.GetValue(null)!) != expected.Item2)
                throw new InvalidDataException(
                    "Qualified client-status field offset changed: " + expected.Item1
                );
        }
        foreach (
            var child in new[]
            {
                (queryOwner.GetProperty("m_actionInfoList")!.PropertyType, "_size", 0x18),
                (queryOwner.GetProperty("m_actionSetupInfoList")!.PropertyType, "_size", 0x18),
                (NativeContract.ResolveType("CGGKFDGOFGN"), "_NMGKEONKAHG_k__BackingField", 0x10),
            }
        )
        {
            var metadata =
                Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
                    child.Item1.GetProperty(child.Item2)!.GetMethod!
                )
                ?? throw new MissingMemberException("Required client-status child field metadata.");
            if (IL2CPP.il2cpp_field_get_offset((IntPtr)metadata.GetValue(null)!) != child.Item3)
                throw new InvalidDataException(
                    "Qualified client-status child field offset changed: "
                        + child.Item1.FullName
                        + "."
                        + child.Item2
                );
        }
        var encrypted = NativeContract.ResolveType("_ee847");
        var storage = NativeContract.ResolveType("_67f24");
        var stored = Activator.CreateInstance(storage)!;
        storage.GetField("e")!.SetValue(stored, long.MinValue);
        storage.GetField("s")!.SetValue(stored, 9007199254741007L);
        storage.GetField("c")!.SetValue(stored, int.MinValue);
        var wrapped = Activator.CreateInstance(encrypted)!;
        encrypted.GetField("m_value")!.SetValue(wrapped, stored);
        var nullable = typeof(Snapshot)
            .GetMethod(nameof(NewNullable), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(encrypted)
            .Invoke(null, [IntPtr.Zero])!;
        var empty = new Snapshot();
        if (empty.Read(nullable) is not null || !empty.Complete)
            throw new InvalidDataException("Empty encrypted date nullable reader failed");
        nullable.GetType().GetProperty("hasValue")!.SetValue(nullable, true);
        nullable.GetType().GetProperty("value")!.SetValue(nullable, wrapped);
        var encryptedSnapshot = new Snapshot();
        encryptedSnapshot.Read(nullable);
        var raw = encryptedSnapshot.Nodes.Single(node => node.type == "_67f24");
        if (
            !encryptedSnapshot.Complete
            || encryptedSnapshot.Nodes.Count != 2
            || !Equals(raw.fields["e"], "-9223372036854775808")
            || !Equals(raw.fields["s"], "9007199254741007")
            || !Equals(raw.fields["c"], int.MinValue)
        )
            throw new InvalidDataException("Native encrypted date nullable storage reader failed");
        GC.KeepAlive(nullable);
        GC.KeepAlive(wrapped);
    }

    private static Il2CppSystem.Nullable<T> NewNullable<T>(IntPtr data = default)
        where T : new()
    {
        var type = NativeContract.ResolveType(
            "Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher"
        );
        var allocate =
            type.GetMethod("BoxNullable", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                "Install the matching Il2CppInterop.HarmonySupport.dll"
            );
        var nativeClass = Il2CppClassPointerStore<Il2CppSystem.Nullable<T>>.NativeClassPtr;
        var pointer = (IntPtr)allocate.Invoke(null, new object[] { nativeClass, data })!;
        if (
            IL2CPP.il2cpp_object_get_class(pointer) != nativeClass
            || IL2CPP.il2cpp_object_get_size(pointer)
                != IL2CPP.il2cpp_class_instance_size(nativeClass)
        )
            throw new InvalidDataException("Nullable allocation changed class or size");
        return new Il2CppSystem.Nullable<T>(pointer);
    }

    internal object? Read(object? value)
    {
        try
        {
            return graph.Read(Prepare(value));
        }
        catch (Exception error)
        {
            return graph.ReadFailure("$root", error);
        }
    }

    internal object? ReadReceiver(object? value, string boundary, long rva)
    {
        if (ResourceReceiverProjection.Applies(boundary, rva))
        {
            try
            {
                if (
                    value is not Il2CppObjectBase owner
                    || owner.Pointer == IntPtr.Zero
                    || !ResourceReceiverProjection.IsOwner(
                        value.GetType().FullName,
                        value.GetType().Module.ScopeName
                    )
                )
                    throw new InvalidDataException(
                        "Resource receiver is not the qualified native owner."
                    );
                var type = value.GetType();
                ResourceReceiverProjection.ValidateOwner(
                    type.FullName,
                    type.Module.ScopeName,
                    owner.Pointer,
                    Il2CppClassPointerStore.GetNativeClassPointer(type),
                    IL2CPP.il2cpp_object_get_class(owner.Pointer)
                );
                return graph.Read(
                    ResourceReceiverProjection.Reference(Network.ObjectIdentity(value))
                );
            }
            catch (Exception error)
            {
                return graph.ReadFailure("$receiver", error);
            }
            finally
            {
                GC.KeepAlive(value);
            }
        }
        if (!ClientStatusProjection.Applies(boundary, rva))
            return Read(value);
        try
        {
            value = Prepare(value);
            if (value is null)
                return null;
            if (
                value is not Il2CppObjectBase native
                || native.Pointer == IntPtr.Zero
                || value.GetType().FullName != "BattleStateVariables"
            )
                throw new InvalidDataException(
                    "Client-status receiver is not the qualified native owner."
                );
            return graph.Read(new ClientStatusOwner(value));
        }
        catch (Exception error)
        {
            return graph.ReadFailure("$receiver", error);
        }
    }

    private static object? Prepare(object? value)
    {
        if (value is null)
            return null;
        value = NativeObjectType.Concrete(value);
        if (value is null)
            return null;
        var type = value.GetType();
        if (type.FullName == "Il2CppSystem.String")
            return value.ToString();
        if (!IsNullable(type))
            return value;
        var fields = Fields.GetOrAdd(type, NativeFields);
        return (bool)fields.Single(f => f.Name == "hasValue").Get!(value)!
            ? Prepare(fields.Single(f => f.Name == "value").Get!(value))
            : null;
    }

    private SnapshotSource Describe(object value)
    {
        var source = DescribeFull(value);
        if (!privacy)
            return source;
        var original = value switch
        {
            ClientStatusOwner owner => owner.Value,
            IdentityItems items => items.Value,
            ScopedItems items => items.Value,
            _ => value,
        };
        var type = original.GetType();
        var declarations =
            source.Fields.Count == 0 ? null : PrivacyDeclarations.GetOrAdd(type, DeclaredFields);
        return source with
        {
            PrivacyType = RuntimeCompatibility.SignatureType(type),
            Declarations = declarations,
            ElementType = source.Items is null ? null : CollectionElement(type),
            AssignableTypes = PrivacyAssignments.GetOrAdd(type, AssignableTypes),
            Protobuf = null,
            ProtobufFallback = null,
            FilteredProtobuf = source.Protobuf is null
                ? null
                : () => Network.ParsePayload(Network.NativeMessageText(original)),
        };
    }

    private static IReadOnlyDictionary<string, string> DeclaredFields(Type type)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields.GetOrAdd(type, NativeFields))
            if (field.ReferenceType is { } fieldType)
                result[field.Name] = RuntimeCompatibility.SignatureType(fieldType);
        if (type.IsValueType)
            foreach (
                var field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                )
            )
                result[field.Name] = RuntimeCompatibility.SignatureType(field.FieldType);
        foreach (var name in new[] { "IIECCBMHJLK", "LMIFNHFLGDI" })
            if (NativeSelections.HasDerived(type, name) && type.GetProperty(name) is { } derived)
                result[name] = RuntimeCompatibility.SignatureType(derived.PropertyType);
        if (type.FullName == "BattleStateVariables")
        {
            result["m_actionInfoList._size"] = "System.Int32";
            result["m_actionSetupInfoList._size"] = "System.Int32";
            result["_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField"] =
                "Blend.ApiSchema.Enum.BattleStatus";
        }
        return result;
    }

    private static string[] AssignableTypes(Type type)
    {
        var types = type.GetInterfaces().Select(RuntimeCompatibility.SignatureType).ToList();
        for (var parent = type.BaseType; parent is not null; parent = parent.BaseType)
            types.Add(RuntimeCompatibility.SignatureType(parent));
        foreach (
            var name in PrivacyNative.InterfaceDeclarations(
                RuntimeCompatibility.SignatureType(type)
            )
        )
        {
            var declaration = NativeContract.ResolveType(name);
            if (
                type.Module.ScopeName != "Blend.dll"
                || declaration.Module.ScopeName != "Blend.dll"
                || !NativeObjectType.FiniteDeclaration(name, declaration.Module.ScopeName)
            )
                throw new InvalidDataException(
                    "Native privacy interface declaration is unqualified."
                );
            Plugin.Compatibility.ValidateInteropType(type);
            Plugin.Compatibility.ValidateInteropType(declaration);
            var declaredClass = Il2CppClassPointerStore.GetNativeClassPointer(declaration);
            var concreteClass = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (declaredClass == IntPtr.Zero || concreteClass == IntPtr.Zero)
                throw new InvalidDataException("Native privacy interface class is unavailable.");
            NativeObjectType.ValidateDeclaration(
                name,
                IL2CPP.il2cpp_class_get_flags(declaredClass)
            );
            NativeObjectType.ValidateResolvedClass(
                type.FullName!,
                declaredClass,
                concreteClass,
                concreteClass,
                IL2CPP.il2cpp_class_is_assignable_from(declaredClass, concreteClass),
                !type.ContainsGenericParameters && typeof(Il2CppObjectBase).IsAssignableFrom(type),
                IL2CPP.il2cpp_class_get_flags(concreteClass)
            );
            types.Add(name);
        }
        return types.ToArray();
    }

    private static string? CollectionElement(Type type)
    {
        if (type.IsArray)
            return RuntimeCompatibility.SignatureType(type.GetElementType()!);
        if (!type.IsConstructedGenericType)
            return null;
        var arguments = type.GenericTypeArguments;
        return arguments.Length == 1 ? RuntimeCompatibility.SignatureType(arguments[0])
            : arguments.Length == 2
            && type.GetGenericTypeDefinition()
                .FullName?.Contains("Dictionary", StringComparison.Ordinal) == true
                ? "Il2CppSystem.Collections.Generic.KeyValuePair`2<"
                    + string.Join(",", arguments.Select(RuntimeCompatibility.SignatureType))
                    + ">"
            : null;
    }

    private static SnapshotSource DescribeFull(object value)
    {
        if (NativeStoredValues.Describe(value) is { } storedValue)
            return storedValue;
        if (value is ClientStatusOwner clientStatus)
        {
            var owner = clientStatus.Value;
            var receiverType = owner.GetType();
            var receiverProjection = Projections.GetOrAdd(receiverType, CompileProjection);
            return ClientStatusProjection.Source(
                Identity(value)!,
                receiverType.FullName!,
                Network
                    .ObjectIdentity(owner)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                name => ReadStoredChild(owner, name),
                ReadStoredChild,
                child =>
                    NativeProjection.Reference(
                        NativeObjectType.ClassName(child),
                        Network.ObjectIdentity(child)
                    ),
                receiverProjection.QueryExcluded
            );
        }
        if (value is IdentityItems projected)
        {
            var original = projected.Value;
            var projectedType = original.GetType();
            return new(
                projectedType.FullName!,
                Identity(projected)!,
                [],
                Items: () =>
                    NativeProjection.ReferenceItems(
                        original is IEnumerable managed
                            ? ManagedItems(managed, original)
                            : Collections
                                .GetOrAdd(projectedType, CollectionReader)(original)
                                .Select(item => ReadChild(original, () => item)),
                        child =>
                            NativeProjection.Reference(
                                NativeObjectType.ClassName(child),
                                Network.ObjectIdentity(child)
                            )
                    ),
                Excluded: ["collection_item_state:native_lifetime_identity_only"]
            );
        }
        if (value is ScopedItems scoped)
        {
            var original = scoped.Value;
            var projectedType = original.GetType();
            return new(
                projectedType.FullName!,
                Identity(scoped)!,
                [],
                Items: () =>
                    (
                        original is IEnumerable managed
                            ? managed.Cast<object?>()
                            : Collections.GetOrAdd(projectedType, CollectionReader)(original)
                    ).Select(item =>
                        ReadChild(original, () => item, null, scoped.Reference, scoped.Declaration)
                    ),
                Excluded: ["qualified_collection_item_state:" + scoped.Reference.StateContract]
            );
        }
        var type = value.GetType();
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
            throw new InvalidDataException("Required native snapshot object is unavailable.");
        var identity = native.Pointer.ToInt64();
        if (NativeFieldPolicy.IsCollection(type) || value is IEnumerable)
            return new(
                type.FullName!,
                identity,
                [],
                Items: () =>
                    value is IEnumerable managed
                        ? ManagedItems(managed, value)
                        : Collections
                            .GetOrAdd(type, CollectionReader)(value)
                            .Select(item => ReadChild(value, () => item))
            );
        NativeFieldPolicy.RequireSemanticOwner(type);
        if (NativeProtobuf.IsMessage(type))
            return new(
                type.FullName!,
                identity,
                [],
                Protobuf: () => NativeProtobuf.Copy(value),
                LifetimeId: Network
                    .ObjectIdentity(value)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProtobufFallback: () =>
                    Fields
                        .GetOrAdd(type, NativeFields)
                        .Where(f => f.Get is not null)
                        .Select(f => new SnapshotMember(
                            f.Name,
                            () => ReadChild(value, () => f.Get!(value), f)
                        ))
                        .ToArray(),
                Lineage: () => BindProtobufChildren(value)
            );
        var projection = Projections.GetOrAdd(type, CompileProjection);
        var fields = projection.Fields;
        if (fields.Length == 0 && NativeFieldPolicy.IsServiceIdentity(type))
            return new(
                type.FullName!,
                identity,
                [],
                Excluded: ["native_api_service_identity_without_gameplay_fields"]
            );
        if (fields.Length == 0)
            throw new MissingMemberException(
                "Required native type has no qualified field readers: " + type.FullName
            );
        var members = projection
            .Readers.Select(f => new SnapshotMember(
                f.Name,
                () => ReadChild(value, () => f.Get!(value), f)
            ))
            .ToList();
        object? FieldValue(string name) => fields.Single(f => f.Name == name).Get!(value);
        members.AddRange(
            NativeSelections
                .Derived(type, FieldValue)
                .Select(member => new SnapshotMember(
                    member.Name,
                    () => ReadChild(value, member.Read)
                ))
        );
        return new(
            type.FullName!,
            identity,
            members,
            Excluded: projection.Excluded,
            Unavailable: projection.Unavailable,
            LifetimeId: typeof(Il2CppSystem.ValueType).IsAssignableFrom(type)
                ? null
                : Network
                    .ObjectIdentity(value)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
    }

    private static object? Identity(object value) =>
        value switch
        {
            ClientStatusOwner query => (
                ((Il2CppObjectBase)query.Value).Pointer.ToInt64(),
                query.Value.GetType(),
                ClientStatusProjection.Contract
            ),
            IdentityItems items => (
                ((Il2CppObjectBase)items.Value).Pointer.ToInt64(),
                items.Value.GetType(),
                "unit_identity_items"
            ),
            ScopedItems items => (
                ((Il2CppObjectBase)items.Value).Pointer.ToInt64(),
                items.Value.GetType(),
                items.Reference
            ),
            Il2CppObjectBase native when native.Pointer != IntPtr.Zero => (
                native.Pointer.ToInt64(),
                value.GetType()
            ),
            _ => null,
        };

    private static Projection CompileProjection(Type type)
    {
        var fields = Fields.GetOrAdd(type, NativeFields);
        return new(
            fields,
            fields.Where(field => field.Get is not null).ToArray(),
            fields
                .Where(field => field.Get is null && !field.Unavailable)
                .Select(field => field.Name + ":" + field.Exclusion)
                .ToArray(),
            fields
                .Where(field => field.Unavailable)
                .Select(field => field.Name + ":" + field.Exclusion)
                .ToArray(),
            type.FullName == "BattleStateVariables"
                ? fields
                    .Where(field =>
                        !ClientStatusProjection.OwnerFields.Contains(
                            field.Name,
                            StringComparer.Ordinal
                        )
                    )
                    .Select(field => field.Name + ":outside_qualified_client_status_read_set")
                    .ToArray()
                : []
        );
    }

    private static object? ReadStoredChild(object owner, string name)
    {
        var field = Fields
            .GetOrAdd(owner.GetType(), NativeFields)
            .Single(item => item.Name == name);
        if (field.Get is null || field.Unavailable)
            throw new MissingMemberException(
                "Required query stored field is unavailable: "
                    + owner.GetType().FullName
                    + "."
                    + name
            );
        return ReadChild(owner, () => field.Get(owner));
    }

    private static object? ReadChild(
        object parent,
        Func<object?> read,
        Field? field = null,
        NativeEdgeReference? itemReference = null,
        Type? itemDeclaration = null
    )
    {
        var child = read();
        if (child is Il2CppObjectBase)
            Network.InheritBinding(
                parent,
                child,
                ChildEdges.GetOrAdd(
                    parent.GetType(),
                    static type => type.FullName + ":generated_field_or_collection_item"
                )
            );
        var edge = field?.EdgeReference ?? itemReference;
        if (child is not null && edge is not null)
        {
            try
            {
                if (
                    NativeObjectType.ReferenceAtEdge(
                        child,
                        field?.ReferenceType ?? itemDeclaration!,
                        edge
                    ) is
                    { } scopedReference
                )
                    return scopedReference;
            }
            finally
            {
                GC.KeepAlive(parent);
            }
        }
        if (child is not null && field?.ReferenceContract is { } referenceContract)
        {
            if (child is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
                throw new InvalidDataException(
                    "Required native reference identity is unavailable."
                );
            try
            {
                var declaredClass = Il2CppClassPointerStore.GetNativeClassPointer(
                    field.ReferenceType!
                );
                var actualClass = IL2CPP.il2cpp_object_get_class(native.Pointer);
                if (
                    declaredClass == IntPtr.Zero
                    || actualClass == IntPtr.Zero
                    || !IL2CPP.il2cpp_class_is_assignable_from(declaredClass, actualClass)
                )
                    throw new InvalidDataException(
                        "Required native reference class is incompatible."
                    );
                return NativeProjection.Reference(
                    NativeObjectType.ClassName(child),
                    Network.ObjectIdentity(child),
                    referenceContract
                );
            }
            finally
            {
                GC.KeepAlive(child);
                GC.KeepAlive(parent);
            }
        }
        child = Prepare(child);
        if (child is Il2CppObjectBase && field?.ItemReference is { } collectionReference)
            return new ScopedItems(
                child,
                field.ReferenceType!.GenericTypeArguments.Single(),
                collectionReference
            );
        if (
            child is Il2CppObjectBase
            && field is not null
            && NativeProjection.IdentityCollection(field.Owner!, field.Name)
        )
            return new IdentityItems(child);
        if (
            child is Il2CppObjectBase
            && field is not null
            && NativeProjection.ReferenceChild(field.Owner!, field.Name, child.GetType().FullName!)
        )
            return NativeProjection.Reference(
                NativeObjectType.ClassName(child),
                Network.ObjectIdentity(child)
            );
        return child;
    }

    internal static string[] BindOwnerLineage(object? value, string boundary, long rva)
    {
        if (ResourceReceiverProjection.Applies(boundary, rva))
            return [];
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
            return [];
        var seen = new HashSet<IntPtr>();
        var failures = new List<string>();
        var pending = new Stack<(object Owner, object? Source, string? Edge)>();
        pending.Push((value, null, null));
        while (pending.TryPop(out var next))
        {
            var owner = next.Owner;
            if (next.Source is { } provenSource)
            {
                try
                {
                    if (Network.EnvironmentProofFor(provenSource) is { } proof)
                        Network.BindEnvironment(owner, proof.IsPrivate, next.Edge!);
                }
                catch (Exception error)
                {
                    failures.Add(next.Edge + ":" + error);
                }
                continue;
            }
            if (owner is not Il2CppObjectBase pointer || !seen.Add(pointer.Pointer))
                continue;
            var type = owner.GetType();
            var sourceNames =
                type.FullName == "BattleStateVariables"
                    ? new[] { "_ENPKCFFLCFM_k__BackingField" }
                    : new[] { "m_battleStateVariables", "CIHHEGKEOED", "m_selfUnit", "__4__this" };
            foreach (var name in sourceNames)
            {
                try
                {
                    var field = Fields
                        .GetOrAdd(type, NativeFields)
                        .SingleOrDefault(f => f.Name == name);
                    if (field?.Get is null || field.Unavailable)
                        continue;
                    if (Prepare(field.Get(owner)) is not Il2CppObjectBase source)
                        continue;
                    pending.Push(
                        (owner, source, type.FullName + "." + name + ":stored_owner_environment")
                    );
                    pending.Push((source, null, null));
                }
                catch (Exception error)
                {
                    failures.Add(type.FullName + "." + name + ":" + error);
                }
            }
        }
        return failures.ToArray();
    }

    internal static string[] BindProtobufChildren(object value)
    {
        try
        {
            var root = Prepare(value);
            if (
                root is not Il2CppObjectBase
                || NativeObjectContexts.Pointer(root) != NativeObjectContexts.Pointer(value)
            )
                throw new InvalidDataException("Native protobuf lineage root identity changed.");
            var proof = Network.BindingStateFor(root);
            if (proof.Context is null && !proof.EnvironmentHasEvidence && !proof.OperationAmbiguous)
                return [];
            var gaps = new HashSet<string>(StringComparer.Ordinal);
            var failures = NativeLineage.BindChildren(
                root,
                parent => Network.ObjectIdentity(parent),
                Children,
                (parent, child, edge) =>
                {
                    var gap = NativeLineage.Transfer(
                        parent,
                        child,
                        edge,
                        Network.BindingStateFor,
                        Network.BindContext,
                        Network.BindEnvironment,
                        Network.InvalidateOperation
                    );
                    if (gap is not null)
                        gaps.Add(gap);
                },
                Network.BindingStateFor
            );
            return failures.Concat(gaps).ToArray();
        }
        catch (Exception error)
        {
            return ["$lineage_root:" + error];
        }

        static IEnumerable<SnapshotMember> Children(object parent)
        {
            var type = parent.GetType();
            if (IsMessageCollection(type))
            {
                var index = 0;
                foreach (var item in Collections.GetOrAdd(type, CollectionReader)(parent))
                {
                    var captured = item;
                    yield return new(
                        type.FullName + ".items[" + index++ + "]",
                        () => ReadMessageChild(captured)
                    );
                }
                yield break;
            }
            foreach (var field in Fields.GetOrAdd(type, NativeFields))
            {
                if (field.Get is not null && NativeLineage.IsStoredChildField(field.Type))
                    yield return new(
                        field.Owner + "." + field.Name + ":generated_schema_field",
                        () => ReadMessageChild(field.Get(parent))
                    );
            }
        }

        static object? ReadMessageChild(object? value)
        {
            if (
                value is Il2CppObjectBase
                && value.GetType().FullName is "Il2CppSystem.Object" or "Google.Protobuf.IMessage"
                && NativeLineage.IsScalarNativeClass(NativeObjectType.ClassName(value))
            )
                return null;
            var child = Prepare(value);
            if (child is null)
                return null;
            return NativeLineage.IsMessageChild(child.GetType(), child is Il2CppObjectBase)
                ? child
                : null;
        }
        static bool IsMessageCollection(Type type) => NativeProtobuf.IsMessageCollection(type);
    }

    private static IEnumerable<object?> ManagedItems(IEnumerable value, object parent)
    {
        foreach (var item in value)
            yield return ReadChild(parent, () => item);
    }

    private static IEnumerable<object?> ReadList<T>(object value)
    {
        var pointer = ((Il2CppObjectBase)value).Pointer;
        var list = new Il2CppSystem.Collections.Generic.IList<T>(pointer);
        var count = new Il2CppSystem.Collections.Generic.ICollection<T>(pointer).Count;
        for (var i = 0; i < count; i++)
            yield return list[i];
    }

    private static Func<object, IEnumerable<object?>> CollectionReader(Type type)
    {
        if (
            type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.IList<>)
        )
            return typeof(Snapshot)
                .GetMethod(nameof(ReadList), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type.GenericTypeArguments)
                .CreateDelegate<Func<object, IEnumerable<object?>>>();
        var get = type.GetMethod("GetEnumerator", Type.EmptyTypes);
        if (get is null && type.IsGenericType && type.GenericTypeArguments.Length == 1)
        {
            var viewType = typeof(Il2CppSystem.Collections.Generic.IEnumerable<>).MakeGenericType(
                type.GenericTypeArguments
            );
            var construct =
                viewType.GetConstructor([typeof(IntPtr)])
                ?? throw new MissingMethodException(
                    "Native enumerable view constructor is unavailable."
                );
            var readView = Collections.GetOrAdd(viewType, CollectionReader);
            return value => readView(construct.Invoke([((Il2CppObjectBase)value).Pointer]));
        }
        if (get is null)
            throw new NotSupportedException(
                "Collection has no qualified native enumeration contract."
            );
        var iterator = get.ReturnType;
        if (
            iterator.IsGenericType
            && iterator.GetGenericTypeDefinition()
                == typeof(Il2CppSystem.Collections.Generic.IEnumerator<>)
        )
        {
            var openInterface = NativeGetter<object>(get);
            var moveInterface = NativeGetter<bool>(
                typeof(Il2CppSystem.Collections.IEnumerator).GetMethod("MoveNext")!
            );
            var readInterface = NativeGetter<object?>(iterator.GetProperty("Current")!.GetMethod!);
            var closeInterface = NativeGetter<object?>(
                typeof(Il2CppSystem.IDisposable).GetMethod("Dispose")!
            );
            return value =>
                Enumerate(openInterface(value), moveInterface, readInterface, closeInterface);
        }
        var next =
            iterator.GetMethod("MoveNext", Type.EmptyTypes)
            ?? throw new NotSupportedException("Enumerator has no concrete MoveNext");
        var current = iterator.GetProperty("Current")!.GetMethod!;
        var dispose = iterator.GetMethod("Dispose", Type.EmptyTypes);
        var open = Getter<object>(get);
        var move = Getter<bool>(next);
        var read = Getter<object?>(current);
        var close = dispose is null ? null : Getter<object?>(dispose);
        return value => Enumerate(open(value), move, read, close);
    }

    private static IEnumerable<object?> Enumerate(
        object iterator,
        Func<object, bool> move,
        Func<object, object?> read,
        Func<object, object?>? close
    )
    {
        try
        {
            while (move(iterator))
                yield return read(iterator);
        }
        finally
        {
            close?.Invoke(iterator);
        }
    }

    private static Func<object, T> Getter<T>(MethodInfo method)
    {
        var value = Expression.Parameter(typeof(object));
        var call = Expression.Call(Expression.Convert(value, method.DeclaringType!), method);
        Expression body =
            method.ReturnType == typeof(void)
                ? Expression.Block(call, Expression.Default(typeof(T)))
                : Expression.Convert(call, typeof(T));
        return Expression.Lambda<Func<object, T>>(body, value).Compile();
    }

    private static unsafe Func<object, T> NativeGetter<T>(MethodInfo method)
    {
        var field =
            Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method)
            ?? throw new MissingMethodException(
                $"Native method pointer unavailable: {method.Name}"
            );
        var read = method.ReturnType == typeof(void) ? null : BoxedReader(method.ReturnType);
        return value =>
        {
            var native = (Il2CppObjectBase)value;
            var pointer = native.Pointer;
            var target = IL2CPP.il2cpp_object_get_virtual_method(
                pointer,
                (IntPtr)field.GetValue(null)!
            );
            if (target == IntPtr.Zero)
                throw new MissingMethodException(method.Name);
            // Generated interface calls do not unbox value-type implementations.
            var receiver = IL2CPP.il2cpp_class_is_valuetype(native.ObjectClass)
                ? IL2CPP.il2cpp_object_unbox(pointer)
                : pointer;
            var exception = IntPtr.Zero;
            var result = IL2CPP.il2cpp_runtime_invoke(target, receiver, null, ref exception);
            GC.KeepAlive(native);
            Il2CppException.RaiseExceptionIfNecessary(exception);
            return read is null ? default! : (T)read(result)!;
        };
    }

    private static Field[] NativeFields(Type type)
    {
        var result = new List<Field>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (
            var owner = type;
            owner is not null && owner != typeof(Il2CppObjectBase);
            owner = owner.BaseType
        )
        {
            var pointers = owner
                .GetFields(
                    BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                )
                .Where(f =>
                    f.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal)
                    && IsInstanceField(f)
                )
                .ToArray();
            var represented = owner
                .GetProperties(
                    BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                )
                .Select(p => "NativeFieldInfoPtr_" + p.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (
                var property in owner
                    .GetProperties(
                        BindingFlags.Instance
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly
                    )
                    .Where(p =>
                        p.GetIndexParameters().Length == 0 && p.GetMethod is { IsStatic: false }
                    )
                    .OrderBy(p => p.MetadataToken)
            )
            {
                var pointerName = "NativeFieldInfoPtr_" + property.Name;
                if (!pointers.Any(p => p.Name == pointerName) && !IsNative(property.GetMethod!))
                {
                    if (NativeSelections.HasDerived(owner, property.Name))
                        continue;
                    var reason =
                        NativeFieldPolicy.Exclusion(property)
                        ?? NativeProjection.Infrastructure(
                            owner.FullName!,
                            property.Name,
                            property.PropertyType.FullName!
                        );
                    if (
                        reason is null
                        && (
                            NativeFieldPolicy.IsCollection(type)
                            || typeof(Il2CppSystem.ValueType).IsAssignableFrom(type)
                        )
                    )
                        continue;
                    if (NativeProtobuf.IsMessage(type))
                        reason ??= "computed_property_not_read_complete_native_protobuf_preserved";
                    if (
                        reason is null
                        && pointers.Any(p =>
                            p.Name == "NativeFieldInfoPtr__" + property.Name + "_k__BackingField"
                        )
                    )
                        reason = "computed_alias_storage_backing_preserved";
                    result.Add(
                        new(
                            property.Name,
                            null,
                            reason ?? "computed_native_value_has_no_proven_projection",
                            owner.FullName,
                            property.PropertyType.FullName,
                            reason is null
                        )
                    );
                    names.Add(property.Name);
                    continue;
                }
                represented.Add(pointerName);
                var field = FieldReader(property) with
                {
                    Owner = owner.FullName,
                    Type = property.PropertyType.FullName,
                    ReferenceContract = NativeProjection.ReferenceContract(property),
                    ReferenceType = property.PropertyType,
                    EdgeReference = NativeProjection.ScopedReference(
                        RuntimeCompatibility.SignatureType(owner),
                        property.Name,
                        RuntimeCompatibility.SignatureType(property.PropertyType),
                        owner.Module.ScopeName
                    ),
                    ItemReference = NativeProjection.ItemReference(
                        RuntimeCompatibility.SignatureType(owner),
                        property.Name,
                        RuntimeCompatibility.SignatureType(property.PropertyType),
                        owner.Module.ScopeName
                    ),
                };
                if (!names.Add(field.Name))
                    field = field with { Name = owner.FullName + "." + field.Name };
                result.Add(field);
            }
            foreach (var pointer in pointers.Where(p => !represented.Contains(p.Name)))
                result.Add(
                    new(
                        owner.FullName + "." + pointer.Name["NativeFieldInfoPtr_".Length..],
                        _ =>
                            throw new MissingMemberException(
                                "Required native field has no generated accessor."
                            ),
                        null
                    )
                );
        }
        return result.ToArray();
    }

    private static bool IsInstanceField(FieldInfo pointer)
    {
        try
        {
            var field = (IntPtr)(pointer.GetValue(null) ?? IntPtr.Zero);
            return field == IntPtr.Zero
                || (IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) == 0;
        }
        catch
        {
            return true;
        }
    }

    private static Field FieldReader(PropertyInfo property)
    {
        try
        {
            return CreateFieldReader(property);
        }
        catch (Exception error)
        {
            return new(
                property.Name,
                _ =>
                    throw new MissingMemberException(
                        "Required native field reader could not bind.",
                        error
                    ),
                null
            );
        }
    }

    private static Field CreateFieldReader(PropertyInfo property)
    {
        if (
            (
                NativeFieldPolicy.Exclusion(property)
                ?? NativeProjection.Infrastructure(
                    property.DeclaringType!.FullName!,
                    property.Name,
                    property.PropertyType.FullName!
                )
            ) is
            { } exclusion
        )
            return new(property.Name, null, exclusion);
        var type = property.PropertyType;
        var nullable = IsNullable(type);
        var value = Expression.Parameter(typeof(object));
        Expression body;
        if (nullable || property.DeclaringType!.IsGenericType)
        {
            var field =
                Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
                    property.GetMethod!
                )
                ?? throw new MissingMemberException(
                    "Required generated native field accessor is unavailable."
                );
            var pointer = Expression.Property(
                Expression.Convert(value, typeof(Il2CppObjectBase)),
                nameof(Il2CppObjectBase.Pointer)
            );
            var boxed = Expression.Call(
                typeof(IL2CPP),
                nameof(IL2CPP.il2cpp_field_get_value_object),
                null,
                Expression.Field(null, field),
                pointer
            );
            var convert = BoxedReader(nullable ? type.GenericTypeArguments[0] : type);
            body = Expression.Invoke(Expression.Constant(convert), boxed);
        }
        else
            body = Expression.Convert(
                Expression.Property(Expression.Convert(value, property.DeclaringType!), property),
                typeof(object)
            );
        return new(
            property.Name,
            Expression.Lambda<Func<object, object?>>(body, value).Compile(),
            null
        );
    }

    private static Func<IntPtr, object?> BoxedReader(Type type)
    {
        var pointer = Expression.Parameter(typeof(IntPtr));
        var convert = Expression.Call(
            typeof(IL2CPP),
            nameof(IL2CPP.PointerToValueGeneric),
            new[] { type },
            pointer,
            Expression.Constant(false),
            Expression.Constant(true)
        );
        var body = Expression.Condition(
            Expression.Equal(pointer, Expression.Constant(IntPtr.Zero)),
            Expression.Constant(null, typeof(object)),
            Expression.Convert(convert, typeof(object))
        );
        return Expression.Lambda<Func<IntPtr, object?>>(body, pointer).Compile();
    }

    private sealed record Field(
        string Name,
        Func<object, object?>? Get,
        string? Exclusion,
        string? Owner = null,
        string? Type = null,
        bool Unavailable = false,
        string? ReferenceContract = null,
        Type? ReferenceType = null,
        NativeEdgeReference? EdgeReference = null,
        NativeEdgeReference? ItemReference = null
    );

    private sealed record Projection(
        Field[] Fields,
        Field[] Readers,
        string[] Excluded,
        string[] Unavailable,
        string[] QueryExcluded
    );

    private sealed record IdentityItems(object Value);

    private sealed record ScopedItems(
        object Value,
        Type Declaration,
        NativeEdgeReference Reference
    );

    private sealed record ClientStatusOwner(object Value);

    private static bool IsNullable(Type type) =>
        type.IsConstructedGenericType
        && type.GetGenericTypeDefinition() == typeof(Il2CppSystem.Nullable<>);

    private static bool IsNative(MethodInfo method)
    {
        try
        {
            return Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
                method
            )
                is not null;
        }
        catch
        {
            return false;
        }
    }
}
