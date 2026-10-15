using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace AtelierCapture;

internal static class NativeStoredValues
{
    private static readonly ConcurrentDictionary<
        Type,
        (string Name, Func<object, object?> Read)[]
    > Readers = new();

    internal static SnapshotSource? Describe(object value)
    {
        var type = value.GetType();
        if (
            !type.IsValueType
            || type.FullName
                is not (
                    "UnityEngine.Vector3"
                    or "UnityEngine.Quaternion"
                    or "Il2CppSystem.DateTime"
                    or "_ee847"
                    or "_67f24"
                )
        )
            return null;
        var readers = Readers.GetOrAdd(type, Bind);
        return new(
            type.FullName!,
            new object(),
            readers
                .Select(field => new SnapshotMember(field.Name, () => field.Read(value)))
                .ToArray()
        );
    }

    private static (string Name, Func<object, object?> Read)[] Bind(Type type)
    {
        var date = type.FullName == "Il2CppSystem.DateTime";
        var names =
            type.FullName == "_ee847" ? new[] { "m_value" }
            : type.FullName == "_67f24" ? new[] { "e", "s", "c" }
            : date ? new[] { "_dateData" }
            : type.FullName == "UnityEngine.Quaternion" ? new[] { "x", "y", "z", "w" }
            : new[] { "x", "y", "z" };
        var fields = type.GetFields(
            BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly
        );
        if (
            fields.Length != names.Length
            || fields.Any(field =>
                !names.Contains(field.Name, StringComparer.Ordinal)
                || (
                    type.FullName == "_ee847"
                        ? field.FieldType.FullName != "_67f24" || !field.FieldType.IsValueType
                    : type.FullName == "_67f24"
                        ? field.FieldType != (field.Name == "c" ? typeof(int) : typeof(long))
                    : field.FieldType != (date ? typeof(ulong) : typeof(float))
                )
            )
        )
            throw new MissingMemberException(
                "Qualified value component layout changed: " + type.FullName
            );
        return names
            .Select(name =>
            {
                var field = fields.Single(field => field.Name == name);
                var value = Expression.Parameter(typeof(object));
                var read = Expression
                    .Lambda<Func<object, object?>>(
                        Expression.Convert(
                            Expression.Field(Expression.Convert(value, type), field),
                            typeof(object)
                        ),
                        value
                    )
                    .Compile();
                return (name, read);
            })
            .ToArray();
    }
}
