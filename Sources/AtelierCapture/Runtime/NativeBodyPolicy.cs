using System.Reflection;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;

namespace AtelierCapture;

internal readonly record struct NativeBodyClassification(
    bool ObserveTarget,
    string? Alias,
    string Reason,
    bool ReceiverAvailable
);

internal sealed record NativeBodyAlias(
    string Name,
    nint MethodInfo,
    nint ReceiverClass,
    nint ArgumentClass,
    bool IsTarget
);

internal sealed class NativeBodyPolicy
{
    private static readonly Target[] Targets =
    [
        new(
            0x3D72CB0,
            "BattleServerControl.SyncAfterAction|PKHEFFANDOI",
            "40534883ec20488bd9488b49204885c9",
            false,
            [
                new("BattleServerControl", "SyncAfterAction", []),
                new("BattleServerControl", "PKHEFFANDOI", []),
            ]
        ),
        new(
            0x2CF1FF0,
            "PLCHDHONCDJ..ctor(Blend.ApiSchema.Model.Ship)",
            "4885d2741e448b421c448b4a20448b52",
            false,
            [
                new("PLCHDHONCDJ", ".ctor", ["Blend.ApiSchema.Model.Ship"]),
                new("EPBDKOKOADI", ".ctor", ["Blend.ApiSchema.Model.CharacterSkillResultRecord"]),
            ]
        ),
        new(
            0x2CEFBA0,
            "JJPPPEBLGOK..ctor(Blend.ApiSchema.Model.SynthesisCombinationRank)",
            "4885d27416488b4220448b421c8b5218",
            false,
            [
                new("JJPPPEBLGOK", ".ctor", ["Blend.ApiSchema.Model.SynthesisCombinationRank"]),
                new("DFLIMJOIHHP", ".ctor", ["Blend.ApiSchema.Model.BattleActiveSkillCommand"]),
                new(
                    "DHBEGLMLHJB",
                    ".ctor",
                    ["Blend.ApiSchema.Model.HouseBuildingPresetHistoryState"]
                ),
                new("JKJNNCGPOIL", ".ctor", ["Blend.ApiSchema.Model.BattleActiveSkill"]),
            ]
        ),
        new(
            0x3754610,
            "BattleDamageControl.OnFlag",
            "440902c3cccccccccccccccccccccccc",
            true,
            [
                new(
                    "BattleDamageControl",
                    "OnFlag",
                    ["BattleDamageControl+HJFPICAALFD&", "BattleDamageControl+HJFPICAALFD"]
                ),
            ]
        ),
    ];

    private readonly bool constructor;
    private readonly NativeBodyAlias[] aliases;

    internal NativeBodyPolicy(
        string boundary,
        bool omitReceiver,
        bool constructor,
        byte[] originalBytes,
        NativeBodyAlias[] aliases
    )
    {
        if (
            string.IsNullOrEmpty(boundary)
            || originalBytes.Length != 16
            || aliases.Length == 0
            || aliases.Count(a => a.IsTarget) != 1
            || aliases.Any(a =>
                string.IsNullOrEmpty(a.Name)
                || a.MethodInfo == 0
                || a.ReceiverClass == 0
                || constructor && a.ArgumentClass == 0
            )
            || aliases.Select(a => a.MethodInfo).Distinct().Count() != aliases.Length
            || constructor
                && aliases.Select(a => a.ReceiverClass).Distinct().Count() != aliases.Length
            || constructor && omitReceiver
        )
            throw new InvalidDataException("Native body classifier binding is incomplete.");
        Boundary = boundary;
        OmitReceiver = omitReceiver;
        this.constructor = constructor;
        OriginalBytes = originalBytes.ToArray();
        this.aliases = aliases.ToArray();
        Aliases = aliases.Select(a => a.Name).ToArray();
    }

    internal int Span => OriginalBytes.Length;
    internal byte[] OriginalBytes { get; }
    internal bool OmitReceiver { get; }
    internal string Boundary { get; }
    internal string[] Aliases { get; }
    internal uint LeafRva { get; private init; }

    internal static bool IsPinnedTarget(MethodBase method, long rva) =>
        FindTarget(method) is { } target && target.Rva == rva;

    internal static NativeBodyPolicy? Create(MethodBase method, nint source)
    {
        if (FindTarget(method) is not { } target)
            return null;
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || source == 0)
            throw new RuntimeCompatibilityException("native_body_platform_or_source");
        NativeHookBodies.RequireAliasCount(target.Rva, target.Aliases.Length);
        var resolved = new NativeBodyAlias[target.Aliases.Length];
        for (var index = 0; index < resolved.Length; index++)
        {
            var alias = target.Aliases[index];
            var owner = NativeContract.ResolveType(alias.Type);
            MethodBase bound =
                alias.Method == ".ctor"
                    ? NativeContract.BindConstructor(owner, target.Rva, alias.Parameters)
                    : NativeContract.Bind(
                        owner,
                        alias.Method,
                        target.Rva,
                        "System.Void",
                        alias.Parameters,
                        false
                    );
            var field =
                Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(bound)
                ?? throw new RuntimeCompatibilityException("native_body_method_info");
            var info = (nint)(field.GetValue(null) ?? nint.Zero);
            if (info == 0 || RuntimeCompatibility.ReadNativePointer(info) != source)
                throw new RuntimeCompatibilityException("native_body_alias_source");
            resolved[index] = new(
                alias.Boundary,
                info,
                NativeClass(owner),
                alias.Method == ".ctor" ? NativeClass(bound.GetParameters()[0].ParameterType) : 0,
                index == 0
            );
        }
        var original = new byte[16];
        BitConverter.TryWriteBytes(
            original.AsSpan(0, 8),
            (long)RuntimeCompatibility.ReadNativePointer(source)
        );
        BitConverter.TryWriteBytes(
            original.AsSpan(8, 8),
            (long)RuntimeCompatibility.ReadNativePointer(source + 8)
        );
        if (!original.AsSpan().SequenceEqual(Convert.FromHexString(target.First16)))
            throw new RuntimeCompatibilityException("native_body_entry_bytes");
        return new NativeBodyPolicy(
            target.Boundary,
            target.OmitReceiver,
            method is ConstructorInfo,
            original,
            resolved
        )
        {
            LeafRva = method is ConstructorInfo ? (uint)target.Rva : 0,
        };
    }

    internal NativeBodyClassification Classify(NativeObservationFrame frame)
    {
        if (OmitReceiver)
            return new(true, aliases[0].Name, "unique_native_body_unused_receiver", false);
        if (frame.ArgumentCount != (constructor ? 1u : 0u))
            return Unclassified("native_body_argument_count");
        var named = NamedAlias(frame.MethodInfo);
        if (!constructor)
        {
            if (
                !TryRead(frame.Receiver, out var receiverClass)
                || receiverClass != aliases[0].ReceiverClass
            )
                return Unclassified("native_body_receiver_class");
            return new(
                true,
                named?.Name,
                named is null
                    ? "shared_native_body_alias_unresolved"
                    : "shared_native_body_alias_method_info",
                true
            );
        }
        if (
            !TryRead(frame.Arguments, out var argumentCell)
            || !TryRead(argumentCell, out var argument)
        )
            return Unclassified("native_body_argument_unavailable");
        // These bodies never touch the receiver for a null source.
        if (argument == 0)
            return named is null
                ? Unclassified("native_body_null_input_identity_unavailable")
                : new(named.IsTarget, named.Name, "native_body_null_input_method_info", false);
        if (frame.MethodInfo != 0 && named is null)
            return Unclassified("native_body_method_info_unknown");
        if (!TryRead(frame.Receiver, out var actualReceiver))
            return Unclassified("native_body_receiver_unavailable");
        NativeBodyAlias? matching = null;
        foreach (var alias in aliases)
            if (alias.ReceiverClass == actualReceiver)
            {
                matching = alias;
                break;
            }
        if (matching is null)
            return Unclassified("native_body_receiver_class");
        if (named is not null && named != matching)
            return Unclassified("native_body_method_info_class_mismatch");
        if (!TryRead(argument, out var actualArgument) || actualArgument != matching.ArgumentClass)
            return Unclassified("native_body_argument_class");
        return new(
            matching.IsTarget,
            matching.Name,
            matching.IsTarget ? "native_body_target_classes" : "native_body_sibling_alias",
            true
        );
    }

    private NativeBodyAlias? NamedAlias(nint info)
    {
        if (info != 0)
            foreach (var alias in aliases)
                if (info == alias.MethodInfo)
                    return alias;
        return null;
    }

    private static NativeBodyClassification Unclassified(string reason) =>
        new(false, null, reason, false);

    private static bool TryRead(nint address, out nint value)
    {
        try
        {
            value = RuntimeCompatibility.ReadNativePointer(address);
            return true;
        }
        catch (RuntimeCompatibilityException)
        {
            value = 0;
            return false;
        }
    }

    private static Target? FindTarget(MethodBase method)
    {
        if (
            method.IsStatic
            || method.IsGenericMethod
            || method.ContainsGenericParameters
            || method.DeclaringType?.Module.ScopeName != "Blend.dll"
        )
            return null;
        foreach (var target in Targets)
        {
            var expected = target.Aliases[0];
            if (
                method.DeclaringType.FullName != expected.Type
                || method.Name != expected.Method
                || (method is ConstructorInfo) != (expected.Method == ".ctor")
                || method is MethodInfo m
                    && RuntimeCompatibility.SignatureType(m.ReturnType) != "System.Void"
                || !method
                    .GetParameters()
                    .Select(p => RuntimeCompatibility.SignatureType(p.ParameterType))
                    .SequenceEqual(expected.Parameters, StringComparer.Ordinal)
            )
                continue;
            return target;
        }
        return null;
    }

    private static nint NativeClass(Type type)
    {
        Plugin.Compatibility.ValidateInteropType(type);
        var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (
            type.Module.ScopeName != "Blend.dll"
            || pointer == 0
            || IL2CPP.il2cpp_class_is_valuetype(pointer)
            || NativeName(pointer) != type.FullName
            || IL2CPP.il2cpp_image_get_name_(IL2CPP.il2cpp_class_get_image(pointer)) != "Blend.dll"
        )
            throw new RuntimeCompatibilityException("native_body_class_identity");
        return pointer;
    }

    private static string NativeName(nint pointer)
    {
        var declaring = IL2CPP.il2cpp_class_get_declaring_type(pointer);
        var name = IL2CPP.il2cpp_class_get_name_(pointer);
        if (string.IsNullOrEmpty(name))
            throw new RuntimeCompatibilityException("native_body_class_name");
        if (declaring != 0)
            return NativeName(declaring) + "+" + name;
        var space = IL2CPP.il2cpp_class_get_namespace_(pointer);
        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }

    private sealed record Target(
        long Rva,
        string Boundary,
        string First16,
        bool OmitReceiver,
        Alias[] Aliases
    );

    private sealed record Alias(string Type, string Method, string[] Parameters)
    {
        internal string Boundary => Type + "." + Method + "(" + string.Join(",", Parameters) + ")";
    }
}
