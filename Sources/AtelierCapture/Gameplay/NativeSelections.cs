using System.Collections.Concurrent;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace AtelierCapture;

internal static class NativeSelections
{
    private static readonly ConcurrentDictionary<Type, Func<object, int, object?>> Selectors =
        new();

    internal static bool HasDerived(Type type, string property) =>
        type.FullName == "ContainerToolWindow" && property == "IIECCBMHJLK"
        || type.FullName == "CharaDetailMenuWindow" && property == "LMIFNHFLGDI";

    internal static IEnumerable<SnapshotMember> Derived(Type type, Func<string, object?> read)
    {
        if (type.FullName == "ContainerToolWindow")
            yield return new(
                "IIECCBMHJLK",
                () => SelectedTool(read("FGEJAGJMLKC"), (int)read("AIDHCNKBDEE")!)
            );
        if (type.FullName == "CharaDetailMenuWindow")
            yield return new(
                "LMIFNHFLGDI",
                () => SelectedCharacter(read("JIICDJJJOFG"), (bool)read("NFBDKHJDCLA")!)
            );
    }

    private static object? SelectedTool(object? values, int index)
    {
        if (values is null || index < 0)
            return null;
        var type = values.GetType();
        if (!type.IsConstructedGenericType || type.GenericTypeArguments.Length != 1)
            throw new InvalidDataException(
                "Original selected-tool collection has no exact generic element type."
            );
        var select = Selectors.GetOrAdd(
            type,
            listType =>
                typeof(NativeSelections)
                    .GetMethod(nameof(ReadAt), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(listType.GenericTypeArguments)
                    .CreateDelegate<Func<object, int, object?>>()
        );
        return select(values, index);
    }

    private static object? ReadAt<T>(object values, int index)
    {
        var pointer = ((Il2CppObjectBase)values).Pointer;
        var list = new Il2CppSystem.Collections.Generic.IList<T>(pointer);
        var count = new Il2CppSystem.Collections.Generic.ICollection<T>(pointer).Count;
        if (index >= count)
            return null;
        var selected = list[index];
        return selected is null ? null : NativeObjectType.Concrete(selected);
    }

    private static object? SelectedCharacter(object? cached, bool needsFallback)
    {
        if (cached is not null && !needsFallback)
            return cached;
        throw new MissingMemberException(
            "Required selected character uses an unqualified native global/source lookup; underlying fields are preserved."
        );
    }
}
