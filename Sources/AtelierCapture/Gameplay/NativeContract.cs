using System.Collections.Concurrent;
using System.Reflection;

namespace AtelierCapture;

internal static class NativeContract
{
    // Concrete-class snapshot reads resolve per child; only unique hits are cached so failures retry.
    private static readonly ConcurrentDictionary<string, Type> Resolved = new(
        StringComparer.Ordinal
    );

    internal static Type ResolveType(string name)
    {
        if (Resolved.TryGetValue(name, out var cached))
            return cached;
        // Distinct folds type forwarders that surface one type through several assemblies.
        var matches = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name.Replace('/', '+'), false))
            .OfType<Type>()
            .Distinct()
            .ToArray();
        return matches.Length switch
        {
            1 => Resolved[name] = matches[0],
            0 => throw new TypeLoadException("Exact native type identity is unavailable: " + name),
            _ => throw new AmbiguousMatchException(
                "Exact native type identity is ambiguous: " + name
            ),
        };
    }

    internal static MethodInfo Bind(
        Type owner,
        string name,
        long rva,
        string returns,
        string[] parameters,
        bool isStatic
    )
    {
        var methods = owner
            .GetMethods(
                BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
            )
            .Where(m =>
                m.DeclaringType == owner
                && m.Name == name
                && Matches(m, returns, parameters, isStatic)
            )
            .ToArray();
        if (methods.Length != 1)
            throw new MissingMethodException(
                "Exact native method signature is unavailable: " + owner.FullName + "." + name
            );
        Plugin.Compatibility.ValidateMethod(methods[0], rva, returns, parameters, isStatic);
        return methods[0];
    }

    internal static ConstructorInfo BindConstructor(Type owner, long rva, string[] parameters)
    {
        var constructors = owner
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(c =>
                c.GetParameters()
                    .Select(p => RuntimeCompatibility.SignatureType(p.ParameterType))
                    .SequenceEqual(parameters, StringComparer.Ordinal)
            )
            .ToArray();
        if (constructors.Length != 1)
            throw new MissingMethodException(
                "Exact native constructor signature is unavailable: " + owner.FullName
            );
        Plugin.Compatibility.ValidateConstructor(constructors[0], rva, parameters);
        return constructors[0];
    }

    internal static bool Matches(
        MethodInfo method,
        string returns,
        IReadOnlyList<string> parameters,
        bool isStatic
    ) =>
        method.IsStatic == isStatic
        && RuntimeCompatibility.SignatureType(method.ReturnType) == returns
        && method
            .GetParameters()
            .Select(p => RuntimeCompatibility.SignatureType(p.ParameterType))
            .SequenceEqual(parameters, StringComparer.Ordinal);
}
