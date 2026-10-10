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
    private static readonly ConcurrentDictionary<Type, Func<object, IEnumerable<object?>>> Collections = new();
    private static HashSet<string> allowed = null!;
    private readonly Dictionary<IntPtr, int> seen = new();
    private int remaining = 12000;
    internal long RetainedBytes { get; private set; } = 4096;

    internal static void Initialize()
    {
        allowed = Plugin.ReadResource<string[]>("SnapshotTypes.json").ToHashSet();
        foreach (var name in allowed) Fields.GetOrAdd(Plugin.ResolveType(name), NativeFields);
    }

    internal static void CheckReaders()
    {
        var list = new Il2CppSystem.Collections.Generic.List<int>();
        list.Add(17);
        list.Add(29);
        var view = new Il2CppSystem.Collections.Generic.IList<int>(list.Pointer);
        foreach (var source in new Il2CppObjectBase[] { list, view })
        {
            var values = Collections.GetOrAdd(source.GetType(), CollectionReader)(source).Take(3).Cast<int>();
            if (!values.SequenceEqual(new[] { 17, 29 })) throw new InvalidDataException("Native list reader failed");
        }
        var map = new Il2CppSystem.Collections.Generic.Dictionary<int, int>();
        map.Add(7, 11);
        map.Add(13, 19);
        map.Add(23, 31);
        map.Remove(13);
        var pairs = Collections.GetOrAdd(map.GetType(), CollectionReader)(map).Take(3).ToArray();
        if (pairs.Length != 2) throw new InvalidDataException("Native dictionary reader did not terminate");
        var fields = Fields.GetOrAdd(pairs[0]!.GetType(), NativeFields);
        var key = fields.Single(f => f.Name == "key").Get!;
        var value = fields.Single(f => f.Name == "value").Get!;
        var actual = pairs.Select(p => ((int)key(p!)!, (int)value(p!)!)).OrderBy(p => p.Item1);
        if (!actual.SequenceEqual(new[] { (7, 11), (23, 31) })) throw new InvalidDataException("Native dictionary values changed");

        var optionalInt = NewNullable<int>();
        var optionalLong = NewNullable<long>();
        if (new Snapshot().Read(optionalInt) is not null || new Snapshot().Read(optionalLong) is not null)
            throw new InvalidDataException("Empty nullable reader failed");
        optionalInt.GetType().GetProperty("hasValue")!.SetValue(optionalInt, true);
        optionalLong.GetType().GetProperty("hasValue")!.SetValue(optionalLong, true);
        optionalInt.value = -17;
        optionalLong.value = 9007199254741007L;
        if (!Equals(new Snapshot().Read(optionalInt), -17) || !Equals(new Snapshot().Read(optionalLong), 9007199254741007L))
            throw new InvalidDataException("Nullable scalar reader failed");
        if (!Equals(new Snapshot().Read(NewNullable<long>(IL2CPP.il2cpp_object_unbox(optionalLong.Pointer))), 9007199254741007L))
            throw new InvalidDataException("Nullable argument copy failed");

        var tuple = new Il2CppSystem.Tuple<int, bool>(23, true);
        var tupleFields = Fields.GetOrAdd(tuple.GetType(), NativeFields);
        if (!Equals(tupleFields.Single(f => f.Name == "m_Item1").Get!(tuple), 23)
            || !Equals(tupleFields.Single(f => f.Name == "m_Item2").Get!(tuple), true))
            throw new InvalidDataException("Native tuple reader failed");
        if (System.Text.Json.JsonSerializer.Serialize(new Snapshot().Read(tuple, 11))
            != System.Text.Json.JsonSerializer.Serialize(new Snapshot().Read(tuple)))
            throw new InvalidDataException("Scalar record was depth limited");
        var nestedType = Plugin.ResolveType("Il2CppSystem.ValueTuple`2").MakeGenericType(typeof(int), typeof(Il2CppSystem.Nullable<int>));
        var nested = (Il2CppObjectBase)Activator.CreateInstance(nestedType)!;
        nestedType.GetProperty("Item1")!.SetValue(nested, 31);
        var nestedField = Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
            nested.GetType().GetProperty("Item2")!.GetMethod!)!;
        var optionalField = Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
            optionalInt.GetType().GetProperty("value")!.GetMethod!)!;
        var nestedPointer = nested.Pointer + (int)IL2CPP.il2cpp_field_get_offset((IntPtr)nestedField.GetValue(null)!);
        var nestedRead = Fields.GetOrAdd(nested.GetType(), NativeFields).Single(f => f.Name == "Item2").Get!;
        if (nestedRead(nested) is not null) throw new InvalidDataException("Empty nested nullable reader failed");
        Marshal.WriteByte(nestedPointer, 1);
        Marshal.WriteInt32(nestedPointer, (int)IL2CPP.il2cpp_field_get_offset((IntPtr)optionalField.GetValue(null)!) - 2 * IntPtr.Size, 47);
        if (!Equals(nestedRead(nested), 47)) throw new InvalidDataException("Nested nullable reader failed");
    }

    private static Il2CppSystem.Nullable<T> NewNullable<T>(IntPtr data = default) where T : new()
    {
        var type = Plugin.ResolveType("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher");
        var allocate = type.GetMethod("BoxNullable", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Install the matching Il2CppInterop.HarmonySupport.dll");
        var nativeClass = Il2CppClassPointerStore<Il2CppSystem.Nullable<T>>.NativeClassPtr;
        var pointer = (IntPtr)allocate.Invoke(null, new object[] { nativeClass, data })!;
        if (IL2CPP.il2cpp_object_get_class(pointer) != nativeClass
            || IL2CPP.il2cpp_object_get_size(pointer) != IL2CPP.il2cpp_class_instance_size(nativeClass))
            throw new InvalidDataException("Nullable allocation changed class or size");
        return new Il2CppSystem.Nullable<T>(pointer);
    }

    internal object? Read(object? value, int depth = 0, bool shallow = false)
    {
        RetainedBytes += 256;
        if (value is null) return null;
        var type = value.GetType();
        if (value is IntPtr or UIntPtr) return new { omitted = "native_pointer" };
        if (type.IsEnum) return new { name = value.ToString(), value = Convert.ToInt64(value) };
        if (value is float f) return float.IsFinite(f) ? f : f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is double d) return double.IsFinite(d) ? d : d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (type.IsPrimitive || value is decimal) return value;
        if (value is string) return new { omitted = "text" };
        var name = type.FullName ?? type.Name;
        if (!CanRead(type)) return new { type = name, omitted = "unapproved_type" };
        if (value is not Il2CppObjectBase native) return new { type = name, omitted = "unsupported_managed_type" };
        if (IsScalarNullable(type))
        {
            var nullableFields = Fields.GetOrAdd(type, NativeFields);
            return (bool)nullableFields.Single(f => f.Name == "hasValue").Get!(value)!
                ? Read(nullableFields.Single(f => f.Name == "value").Get!(value), depth) : null;
        }
        if (seen.TryGetValue(native.Pointer, out var previous)) return new { reference = previous };
        if (--remaining < 0 || (depth > 10 && !IsScalarRecord(type))) return new { type = name, omitted = "snapshot_limit" };
        var id = seen.Count + 1;
        seen[native.Pointer] = id;
        if (name.StartsWith("Blend.ApiSchema.Model.", StringComparison.Ordinal)
            || name.StartsWith("Blend.ApiSchema.Api.", StringComparison.Ordinal))
        {
            var json = ((Il2CppSystem.Object)value).ToString();
            RetainedBytes += json.Length * 64L;
            return new { id, type = name, payload = Plugin.Privacy.Clean(Privacy.Parse(json)) };
        }
        if ((name.StartsWith("Il2CppSystem.Collections.", StringComparison.Ordinal)
            && !typeof(Il2CppSystem.ValueType).IsAssignableFrom(type)) || value is IEnumerable)
            return Collection(value, type, id, depth);
        var fields = new Dictionary<string, object?>();
        foreach (var property in Fields.GetOrAdd(type, NativeFields))
        {
            if (shallow && !property.Scalar) continue;
            RetainedBytes += 256;
            if (property.Get is null)
            {
                fields[property.Name] = property.Omitted;
                continue;
            }
            if (remaining <= 0) { fields["$omitted"] = "snapshot_limit"; break; }
            try { fields[property.Name] = Read(property.Get(value), depth + 1); }
            catch (Exception ex)
            {
                Plugin.ReportFailure("snapshot_field", ex);
                fields[property.Name] = new { error = (ex.InnerException ?? ex).GetType().Name };
            }
        }
        return new { id, type = name, shallow, fields };
    }

    private object Collection(object value, Type type, int id, int depth)
    {
        var items = new List<object?>();
        IEnumerator? enumerator = null;
        var more = false;
        string? error = null;
        try
        {
            enumerator = value is IEnumerable managed ? managed.GetEnumerator()
                : Collections.GetOrAdd(type, CollectionReader)(value).GetEnumerator();
            while ((more = enumerator.MoveNext()) && items.Count < 4096 && remaining > 0)
                items.Add(Read(enumerator.Current, depth + 1));
        }
        catch (Exception ex)
        {
            Plugin.ReportFailure("snapshot_collection", ex);
            error = (ex.InnerException ?? ex).GetType().Name;
        }
        finally
        {
            try { (enumerator as IDisposable)?.Dispose(); }
            catch (Exception ex)
            {
                Plugin.ReportFailure("snapshot_collection_dispose", ex);
                error ??= (ex.InnerException ?? ex).GetType().Name;
            }
        }
        return new { id, type = type.FullName, items, truncated = more, error };
    }

    private static IEnumerable<object?> ReadList<T>(object value)
    {
        var pointer = ((Il2CppObjectBase)value).Pointer;
        var list = new Il2CppSystem.Collections.Generic.IList<T>(pointer);
        var count = new Il2CppSystem.Collections.Generic.ICollection<T>(pointer).Count;
        for (var i = 0; i < count; i++) yield return list[i];
    }

    private static Func<object, IEnumerable<object?>> CollectionReader(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.IList<>))
            return typeof(Snapshot).GetMethod(nameof(ReadList), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type.GenericTypeArguments).CreateDelegate<Func<object, IEnumerable<object?>>>();
        var get = type.GetMethod("GetEnumerator", Type.EmptyTypes)
            ?? throw new NotSupportedException("Collection has no concrete enumerator");
        var iterator = get.ReturnType;
        var next = iterator.GetMethod("MoveNext", Type.EmptyTypes)
            ?? throw new NotSupportedException("Enumerator has no concrete MoveNext");
        var current = iterator.GetProperty("Current")!.GetMethod!;
        var dispose = iterator.GetMethod("Dispose", Type.EmptyTypes);
        var open = Getter<object>(get);
        var move = Getter<bool>(next);
        var read = Getter<object?>(current);
        var close = dispose is null ? null : Getter<object?>(dispose);
        return value => Enumerate(open(value), move, read, close);
    }

    private static IEnumerable<object?> Enumerate(object iterator, Func<object, bool> move,
        Func<object, object?> read, Func<object, object?>? close)
    {
        try { while (move(iterator)) yield return read(iterator); }
        finally { close?.Invoke(iterator); }
    }

    private static Func<object, T> Getter<T>(MethodInfo method)
    {
        var value = Expression.Parameter(typeof(object));
        var call = Expression.Call(Expression.Convert(value, method.DeclaringType!), method);
        Expression body = method.ReturnType == typeof(void) ? Expression.Block(call, Expression.Default(typeof(T))) : Expression.Convert(call, typeof(T));
        return Expression.Lambda<Func<object, T>>(body, value).Compile();
    }

    private static Field[] NativeFields(Type type) => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(p => p.PropertyType != typeof(string) && p.GetIndexParameters().Length == 0
            && p.GetMethod is { IsStatic: false } method && IsNative(method))
        .OrderBy(p => p.MetadataToken).Select(FieldReader).ToArray();

    private static Field FieldReader(PropertyInfo property)
    {
        var type = property.PropertyType;
        if ((property.DeclaringType?.FullName == "UIAssetLoader" && property.Name is "m_loadedPrefabTable" or "m_imageToAddressTable")
            || (property.DeclaringType?.FullName == "AssetLoader" && property.Name == "m_handlesCache"))
            return new Field(property.Name, false, null, new { omitted = "render_resource_cache" });
        var nullable = IsScalarNullable(type);
        Func<object, object?>? read = null;
        if (CanRead(type) || nullable)
        {
            var value = Expression.Parameter(typeof(object));
            Expression body;
            if (nullable || property.DeclaringType!.IsGenericType)
            {
                var field = Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(property.GetMethod!)!;
                var pointer = Expression.Property(Expression.Convert(value, typeof(Il2CppObjectBase)), nameof(Il2CppObjectBase.Pointer));
                var boxed = Expression.Call(typeof(IL2CPP), nameof(IL2CPP.il2cpp_field_get_value_object), null, Expression.Field(null, field), pointer);
                var convert = BoxedReader(nullable ? type.GenericTypeArguments[0] : type);
                body = Expression.Invoke(Expression.Constant(convert), boxed);
            }
            else body = Expression.Convert(Expression.Property(Expression.Convert(value, property.DeclaringType!), property), typeof(object));
            read = Expression.Lambda<Func<object, object?>>(body, value).Compile();
        }
        return new Field(property.Name, type.IsPrimitive || type.IsEnum || nullable, read,
            new { type = type.FullName, omitted = "unapproved_type" });
    }

    private static Func<IntPtr, object?> BoxedReader(Type type)
    {
        var pointer = Expression.Parameter(typeof(IntPtr));
        var convert = Expression.Call(typeof(IL2CPP), nameof(IL2CPP.PointerToValueGeneric), new[] { type }, pointer, Expression.Constant(false), Expression.Constant(true));
        var body = Expression.Condition(Expression.Equal(pointer, Expression.Constant(IntPtr.Zero)),
            Expression.Constant(null, typeof(object)), Expression.Convert(convert, typeof(object)));
        return Expression.Lambda<Func<IntPtr, object?>>(body, pointer).Compile();
    }

    private sealed record Field(string Name, bool Scalar, Func<object, object?>? Get, object Omitted);

    private static bool IsScalarNullable(Type type) => type.IsConstructedGenericType
        && type.GetGenericTypeDefinition() == typeof(Il2CppSystem.Nullable<>)
        && (type.GenericTypeArguments[0].IsPrimitive || type.GenericTypeArguments[0].IsEnum)
        && type.GenericTypeArguments[0] != typeof(IntPtr) && type.GenericTypeArguments[0] != typeof(UIntPtr);

    private static bool IsScalarRecord(Type type)
    {
        var fields = Fields.GetOrAdd(type, NativeFields);
        return fields.Length > 0 && fields.All(f => f.Scalar && f.Get is not null);
    }

    private static bool CanRead(Type type)
    {
        if (type == typeof(IntPtr) || type == typeof(UIntPtr)) return false;
        var name = type.FullName ?? type.Name;
        var tuple = type.IsConstructedGenericType
            && (type.GetGenericTypeDefinition() == typeof(Il2CppSystem.Tuple<,>)
                || type.GetGenericTypeDefinition().FullName == "Il2CppSystem.ValueTuple`2")
            && type.GenericTypeArguments.Any(CanRead);
        return type.IsPrimitive || type.IsEnum || type == typeof(decimal) || IsScalarNullable(type) || tuple || allowed.Contains(name)
            || name.StartsWith("Blend.ApiSchema.Model.", StringComparison.Ordinal)
            || name.StartsWith("Blend.ApiSchema.Api.", StringComparison.Ordinal)
            || name.StartsWith("Il2CppSystem.Collections.", StringComparison.Ordinal)
            || typeof(IEnumerable).IsAssignableFrom(type);
    }

    private static bool IsNative(MethodInfo method)
    {
        try { return Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(method) is not null; }
        catch { return false; }
    }
}
