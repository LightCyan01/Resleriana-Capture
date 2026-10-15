using System.Collections.Concurrent;
using System.Linq.Expressions;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace AtelierCapture;

internal static class NativeObjectType
{
    private static readonly ConcurrentDictionary<Type, Func<IntPtr, object>> Wrap = new();

    internal static object WrapPointer(Type type, IntPtr pointer) =>
        Wrap.GetOrAdd(type, Wrapper)(pointer);

    internal static string ClassName(object value)
    {
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
            throw new InvalidDataException("Native reference class is unavailable.");
        try
        {
            return NativeName(IL2CPP.il2cpp_object_get_class(native.Pointer));
        }
        finally
        {
            GC.KeepAlive(value);
        }
    }

    internal static object? Concrete(object value)
    {
        if (
            value is not Il2CppObjectBase native
            || !RequiresConcrete(value.GetType().FullName, value.GetType().Module.ScopeName)
        )
            return value;
        object? result = null;
        var name = "unavailable";
        try
        {
            var declared = value.GetType();
            var finite = FiniteDeclaration(declared.FullName, declared.Module.ScopeName);
            if (finite)
                Plugin.Compatibility.ValidateInteropType(declared);
            var pointer = native.Pointer;
            if (pointer == IntPtr.Zero)
                throw new InvalidDataException("Native concrete reference is null.");
            var nativeClass = IL2CPP.il2cpp_object_get_class(pointer);
            if (nativeClass == IntPtr.Zero)
                throw new InvalidDataException("Native concrete class is null.");
            name = NativeName(nativeClass);
            var declaredClass = Il2CppClassPointerStore.GetNativeClassPointer(declared);
            if (declaredClass == IntPtr.Zero)
                throw new InvalidDataException("Native declared class is null.");
            var assignable = IL2CPP.il2cpp_class_is_assignable_from(declaredClass, nativeClass);
            if (finite)
            {
                if (NativeName(declaredClass) != declared.FullName)
                    throw new InvalidDataException(
                        "Native declared class name does not match its generated wrapper."
                    );
                ValidateDeclaration(
                    declared.FullName!,
                    IL2CPP.il2cpp_class_get_flags(declaredClass)
                );
            }
            if (name == "System.String")
            {
                ValidateResolvedClass(
                    name,
                    declaredClass,
                    nativeClass,
                    nativeClass,
                    assignable,
                    true,
                    0
                );
                return IL2CPP.Il2CppStringToManaged(pointer);
            }
            var wrapperName = name.StartsWith("System.", StringComparison.Ordinal)
                ? "Il2Cpp" + name
                : name;
            var type = NativeContract.ResolveType(wrapperName);
            if (finite)
                Plugin.Compatibility.ValidateInteropType(type);
            var closedWrapper =
                !type.ContainsGenericParameters && typeof(Il2CppObjectBase).IsAssignableFrom(type);
            var registeredClass = closedWrapper
                ? Il2CppClassPointerStore.GetNativeClassPointer(type)
                : IntPtr.Zero;
            ValidateResolvedClass(
                name,
                declaredClass,
                nativeClass,
                registeredClass,
                assignable,
                closedWrapper,
                IL2CPP.il2cpp_class_get_flags(nativeClass)
            );
            result = WrapPointer(type, pointer);
            var resultPointer = ((Il2CppObjectBase)result).Pointer;
            ValidateWrappedReference(
                name,
                pointer,
                resultPointer,
                nativeClass,
                resultPointer == IntPtr.Zero
                    ? IntPtr.Zero
                    : IL2CPP.il2cpp_object_get_class(resultPointer)
            );
            return result;
        }
        catch (Exception error)
        {
            throw new InvalidDataException(
                "Required native concrete class is unavailable: " + name,
                error
            );
        }
        finally
        {
            GC.KeepAlive(result);
            GC.KeepAlive(value);
        }
    }

    internal static bool RequiresConcrete(string? name, string? module) =>
        name is "Il2CppSystem.Object" or "Google.Protobuf.IMessage"
        || FiniteDeclaration(name, module);

    internal static bool FiniteDeclaration(string? name, string? module) =>
        module == "Blend.dll"
            && name
                is "FCMCKDCGGEB"
                    or "KAKLKJIIIHB"
                    or "PLIHKAHEAEN"
                    or "BattleUnitMoveControl+JPOMNKKPIFC+COBONNODPIB"
        || module == "Blend.Framework.dll" && name == "FHOEJNIEFIL";

    internal static void ValidateDeclaration(string name, int flags)
    {
        var expected = name == "BattleUnitMoveControl+JPOMNKKPIFC+COBONNODPIB" ? 0x80 : 0xA0;
        if ((flags & 0xA0) != expected)
            throw new InvalidDataException(
                "Native declaration flags do not match the pinned contract: " + name
            );
    }

    internal static void ValidateResolvedClass(
        string name,
        IntPtr declaredClass,
        IntPtr actualClass,
        IntPtr registeredClass,
        bool assignable,
        bool closedWrapper,
        int actualFlags
    )
    {
        if (
            declaredClass == IntPtr.Zero
            || actualClass == IntPtr.Zero
            || registeredClass != actualClass
            || !assignable
            || !closedWrapper
            || (actualFlags & 0xA0) != 0
        )
            throw new InvalidDataException(
                "Native concrete class identity is not qualified: " + name
            );
    }

    internal static void ValidateWrappedReference(
        string name,
        IntPtr pointer,
        IntPtr wrappedPointer,
        IntPtr actualClass,
        IntPtr wrappedClass
    )
    {
        if (
            pointer == IntPtr.Zero
            || wrappedPointer != pointer
            || actualClass == IntPtr.Zero
            || wrappedClass != actualClass
        )
            throw new InvalidDataException("Native concrete wrapper identity changed: " + name);
    }

    private static string NativeName(IntPtr nativeClass)
    {
        var owner = IL2CPP.il2cpp_class_get_declaring_type(nativeClass);
        var name =
            IL2CPP.il2cpp_class_get_name_(nativeClass)
            ?? throw new MissingMemberException("Native concrete class name is unavailable.");
        if (owner != IntPtr.Zero)
            return NativeName(owner) + "+" + name;
        var space = IL2CPP.il2cpp_class_get_namespace_(nativeClass);
        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }

    private static Func<IntPtr, object> Wrapper(Type type)
    {
        var constructor =
            type.GetConstructor([typeof(IntPtr)])
            ?? throw new MissingMethodException("Native concrete pointer wrapper is unavailable.");
        var pointer = Expression.Parameter(typeof(IntPtr));
        return Expression
            .Lambda<Func<IntPtr, object>>(
                Expression.Convert(Expression.New(constructor, pointer), typeof(object)),
                pointer
            )
            .Compile();
    }
}
