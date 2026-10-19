using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Common;

namespace AtelierCapture;

internal sealed class RuntimeCompatibilityException(string code)
    : InvalidOperationException($"Capture compatibility check failed: {code}")
{
    internal string Code { get; } = code;
}

internal sealed class RuntimeCompatibility
{
    internal const string GameAssemblyHash =
        "35868ee52cb199b018e2ced962dd233a9f4d200a7e5d8e9903e7f4607f21f5ea";
    internal const string MetadataHash =
        "af138395b956bc835f99b2fd140a4c5015781e56c9cab3052bdb7e69065cc9b0";
    internal const string ExecutableHash =
        "6fdcb1607398bfd58c6f83ec0908781e82c65c671376e9d1444001c55c75731a";
    private readonly Dictionary<MethodBase, long> validated = [];
    private readonly object gate = new();
    private readonly List<string> failures = [];
    private readonly ConcurrentDictionary<string, object> interopIdentities = new(
        StringComparer.OrdinalIgnoreCase
    );
    private string? interopDirectory;
    private object? configurationIdentity;
    private long nativeBase;

    internal bool ClientVerified { get; private set; }
    internal bool ApiVerified { get; private set; }
    internal bool NativeGameplayVerified => ApiVerified;
    internal object Status
    {
        get
        {
            return new
            {
                client_verified = ClientVerified,
                safe_api_runtime_verified = ApiVerified,
                native_gameplay_runtime_verified = NativeGameplayVerified,
                native_hook_abi_qualification = "per_contract_required",
                native_detour_body_policy = "fingerprint_pinned_globally_unique_rva_only",
                failures = failures.ToArray(),
                configuration = configurationIdentity,
                loaded_interop = interopIdentities
                    .OrderBy(p => p.Key)
                    .Select(p => p.Value)
                    .ToArray(),
            };
        }
    }

    internal static RuntimeCompatibility Inspect(string gameRoot, string certificatePath)
    {
        var result = new RuntimeCompatibility();
        try
        {
            if (
                !OperatingSystem.IsWindows()
                || RuntimeInformation.ProcessArchitecture != Architecture.X64
            )
                throw new RuntimeCompatibilityException("windows_x64_required");
            using var certificate = JsonDocument.Parse(File.ReadAllBytes(certificatePath));
            var data = certificate.RootElement;
            if (
                data.GetProperty("schema").GetInt32() != 1
                || data.GetProperty("runtime_major").GetInt32() != 10
            )
                throw new RuntimeCompatibilityException("certificate_schema_or_runtime");
            var client = data.GetProperty("supported_client");
            if (
                !HashMatches(
                    client.GetProperty("gameassembly_sha256").GetString(),
                    GameAssemblyHash
                )
                || !HashMatches(client.GetProperty("metadata_sha256").GetString(), MetadataHash)
                || !HashMatches(client.GetProperty("executable_sha256").GetString(), ExecutableHash)
                || client.GetProperty("game_version").GetString() != "2.16.0"
                || client.GetProperty("unity_version").GetString() != "2022.3.62f2"
            )
                throw new RuntimeCompatibilityException("unsupported_client_certificate");
            VerifyFile(gameRoot, "GameAssembly.dll", GameAssemblyHash, null);
            VerifyFile(
                gameRoot,
                "AtelierResleriana_Data/il2cpp_data/Metadata/global-metadata.dat",
                MetadataHash,
                null
            );
            VerifyFile(gameRoot, "AtelierResleriana.exe", ExecutableHash, null);
            result.ClientVerified = true;
            if (
                Environment.Version.Major != 10
                || Environment.Version.ToString() != data.GetProperty("runtime_version").GetString()
            )
                throw new RuntimeCompatibilityException("running_runtime_version");
            var verifiedPaths = VerifyRuntimeFiles(gameRoot, data.GetProperty("runtime_files"));
            result.configurationIdentity = VerifyConfiguration(
                gameRoot,
                data.GetProperty("configuration_contract")
            );
            foreach (
                var required in new[]
                {
                    "BepInEx/core/BepInEx.Core.dll",
                    "BepInEx/core/BepInEx.Unity.IL2CPP.dll",
                    "BepInEx/core/Il2CppInterop.Runtime.dll",
                    "BepInEx/core/Il2CppInterop.Common.dll",
                    "BepInEx/core/Il2CppInterop.HarmonySupport.dll",
                    "BepInEx/core/0Harmony.dll",
                }
            )
                if (!verifiedPaths.Contains(Path.GetFullPath(Path.Combine(gameRoot, required))))
                    throw new RuntimeCompatibilityException("runtime_certificate_incomplete");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                    continue;
                if (
                    assembly.GetName().Name
                    is "BepInEx.Core"
                        or "BepInEx.Unity.IL2CPP"
                        or "Il2CppInterop.Runtime"
                        or "Il2CppInterop.Common"
                        or "Il2CppInterop.HarmonySupport"
                        or "0Harmony"
                )
                    if (!verifiedPaths.Contains(Path.GetFullPath(assembly.Location)))
                        throw new RuntimeCompatibilityException(
                            "loaded_runtime_outside_certificate"
                        );
            }
            using var process = Process.GetCurrentProcess();
            var module =
                process
                    .Modules.Cast<ProcessModule>()
                    .SingleOrDefault(m =>
                        m.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)
                    )
                ?? throw new RuntimeCompatibilityException("native_module_not_loaded");
            if (
                !Path.GetFullPath(module.FileName)
                    .Equals(
                        Path.GetFullPath(Path.Combine(gameRoot, "GameAssembly.dll")),
                        StringComparison.OrdinalIgnoreCase
                    )
            )
                throw new RuntimeCompatibilityException("native_module_path");
            result.nativeBase = module.BaseAddress.ToInt64();
            result.interopDirectory =
                Path.GetFullPath(Path.Combine(gameRoot, "BepInEx", "interop"))
                + Path.DirectorySeparatorChar;
            result.ApiVerified = true;
        }
        catch (Exception error)
        {
            result.failures.Add(
                error is RuntimeCompatibilityException failure ? failure.Code : error.GetType().Name
            );
        }
        return result;
    }

    internal static HashSet<string> VerifyRuntimeFiles(string root, JsonElement files)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateArray())
        {
            var path =
                file.GetProperty("path").GetString()
                ?? throw new RuntimeCompatibilityException("runtime_file_path");
            var full = ContainedPath(root, path);
            if (!paths.Add(full))
                throw new RuntimeCompatibilityException("duplicate_runtime_file");
            VerifyFile(
                root,
                path,
                file.GetProperty("sha256").GetString(),
                file.GetProperty("size").GetInt64()
            );
        }
        return paths;
    }

    internal static object VerifyConfiguration(string root, JsonElement contract)
    {
        var entries = contract.EnumerateArray().ToArray();
        var expected = new Dictionary<(string Path, string Section, string Key), string>
        {
            [("BepInEx/config/BepInEx.cfg", "IL2CPP", "UnityBaseLibrariesSource")] =
                "2022.3.62.zip",
            [("BepInEx/config/BepInEx.cfg", "IL2CPP", "GlobalMetadataPath")] =
                "{GameDataPath}/il2cpp_data/Metadata/global-metadata.dat",
            [("BepInEx/config/BepInEx.cfg", "IL2CPP", "IL2CPPInteropAssembliesPath")] = "{BepInEx}",
        };
        if (entries.Length != expected.Count)
            throw new RuntimeCompatibilityException("configuration_contract");
        var declared = new HashSet<(string Path, string Section, string Key)>();
        foreach (var entry in entries)
        {
            var identity = (
                entry.GetProperty("path").GetString() ?? "",
                entry.GetProperty("section").GetString() ?? "",
                entry.GetProperty("key").GetString() ?? ""
            );
            if (
                !declared.Add(identity)
                || !expected.TryGetValue(identity, out var value)
                || entry.GetProperty("value").GetString() != value
            )
                throw new RuntimeCompatibilityException("configuration_contract");
        }
        var identities = new List<object>();
        var found = new Dictionary<(string Path, string Section, string Key), int>();
        foreach (
            var relative in expected.Keys.Select(key => key.Path).Distinct(StringComparer.Ordinal)
        )
        {
            var bytes = File.ReadAllBytes(ContainedPath(root, relative));
            identities.Add(
                new
                {
                    path = relative,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    size = bytes.LongLength,
                }
            );
            var section = "";
            foreach (var original in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                var line = original.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line[0] is '#' or ';')
                    continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }
                var equals = line.IndexOf('=');
                if (equals < 0)
                    continue;
                var identity = (relative, section, line[..equals].Trim());
                if (!expected.TryGetValue(identity, out var value))
                    continue;
                found[identity] = found.GetValueOrDefault(identity) + 1;
                if (line[(equals + 1)..].Trim() != value)
                    throw new RuntimeCompatibilityException(
                        identity.Item3 switch
                        {
                            "UnityBaseLibrariesSource" => "offline_unity_source",
                            _ => "interop_input_configuration",
                        }
                    );
            }
        }
        if (
            found.GetValueOrDefault(
                ("BepInEx/config/BepInEx.cfg", "IL2CPP", "UnityBaseLibrariesSource")
            ) != 1
        )
            throw new RuntimeCompatibilityException("offline_unity_source_missing_or_duplicate");
        if (expected.Keys.Any(key => found.GetValueOrDefault(key) != 1))
            throw new RuntimeCompatibilityException(
                "interop_input_configuration_missing_or_duplicate"
            );
        return new
        {
            files = identities.ToArray(),
            required_contract = "offline_unity_libraries_2022.3.62_pinned_metadata_interop_paths",
            logging_preferences = "user_owned_mutable",
            localization_preferences = "user_owned_mutable",
        };
    }

    internal static string ContainedPath(string root, string relative)
    {
        if (
            Path.IsPathRooted(relative)
            || relative.Contains(':')
            || relative.Split('/', '\\').Any(p => p is ".." or "." or "")
        )
            throw new RuntimeCompatibilityException("runtime_file_path");
        var directory =
            Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(
            Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar))
        );
        if (!full.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            throw new RuntimeCompatibilityException("runtime_file_outside_root");
        return full;
    }

    private static void VerifyFile(string root, string relative, string? expected, long? size)
    {
        var path = ContainedPath(root, relative);
        var info = new FileInfo(path);
        if (!info.Exists || size is < 0 || size is not null && info.Length != size)
            throw new RuntimeCompatibilityException("runtime_file_missing_or_size");
        using var input = File.OpenRead(path);
        if (!HashMatches(expected, Convert.ToHexString(SHA256.HashData(input))))
            throw new RuntimeCompatibilityException("runtime_file_hash");
    }

    private static bool HashMatches(string? first, string? second) =>
        first is { Length: 64 }
        && first.All(Uri.IsHexDigit)
        && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    internal static string SignatureType(Type type) =>
        type.IsByRef ? SignatureType(type.GetElementType()!) + "&"
        : type.IsArray ? SignatureType(type.GetElementType()!) + "[]"
        : type.IsConstructedGenericType
            ? type.GetGenericTypeDefinition().FullName
                + "<"
                + string.Join(",", type.GenericTypeArguments.Select(SignatureType))
                + ">"
        : type.FullName ?? type.Name;

    internal void ValidateMethod(
        MethodInfo method,
        long rva,
        string returnType,
        IReadOnlyList<string> parameterTypes,
        bool isStatic
    )
    {
        if (!ApiVerified)
            throw new RuntimeCompatibilityException("runtime_not_verified");
        if (
            rva <= 0
            || method.IsStatic != isStatic
            || SignatureType(method.ReturnType) != returnType
            || !method
                .GetParameters()
                .Select(p => SignatureType(p.ParameterType))
                .SequenceEqual(parameterTypes, StringComparer.Ordinal)
        )
            throw new RuntimeCompatibilityException("native_signature");
        ValidatePointer(method, rva);
    }

    internal void ValidateConstructor(
        ConstructorInfo constructor,
        long rva,
        IReadOnlyList<string> parameterTypes
    )
    {
        if (!ApiVerified)
            throw new RuntimeCompatibilityException("runtime_not_verified");
        if (
            rva <= 0
            || constructor.IsStatic
            || !constructor
                .GetParameters()
                .Select(p => SignatureType(p.ParameterType))
                .SequenceEqual(parameterTypes, StringComparer.Ordinal)
        )
            throw new RuntimeCompatibilityException("native_constructor_signature");
        ValidatePointer(constructor, rva);
    }

    internal void ValidateInteropType(Type type)
    {
        if (!ApiVerified)
            throw new RuntimeCompatibilityException("runtime_not_verified");
        var assembly = type.Assembly;
        var path =
            assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)
                ? ""
                : Path.GetFullPath(assembly.Location);
        if (
            interopDirectory is null
            || !path.StartsWith(interopDirectory, StringComparison.OrdinalIgnoreCase)
        )
            throw new RuntimeCompatibilityException("native_owner_outside_game_interop");
        if (!interopIdentities.ContainsKey(path))
        {
            using var input = File.OpenRead(path);
            interopIdentities.TryAdd(
                path,
                new
                {
                    assembly = assembly.FullName,
                    path,
                    sha256 = Convert.ToHexString(SHA256.HashData(input)),
                    size = input.Length,
                    provenance = "generated_in_installed_game_observed_module_identity_native_class_or_method_checked_separately",
                }
            );
        }
    }

    private void ValidatePointer(MethodBase method, long rva)
    {
        ValidateInteropType(
            method.DeclaringType ?? throw new RuntimeCompatibilityException("native_owner_assembly")
        );
        var field =
            Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method)
            ?? throw new RuntimeCompatibilityException("generated_method_pointer");
        var info = (IntPtr)(field.GetValue(null) ?? IntPtr.Zero);
        if (info == IntPtr.Zero || ReadNativePointer(info).ToInt64() != checked(nativeBase + rva))
            throw new RuntimeCompatibilityException("native_method_rva");
        lock (gate)
            validated[method] = rva;
    }

    internal static IntPtr ReadNativePointer(IntPtr address)
    {
        if (!IsReadablePointer(address))
            throw new RuntimeCompatibilityException("unreadable_native_method_pointer");
        if (
            !OperatingSystem.IsWindows()
            || !ReadProcessMemory(
                new IntPtr(-1),
                address,
                out var value,
                (nuint)IntPtr.Size,
                out var read
            )
            || read != (nuint)IntPtr.Size
        )
            throw new RuntimeCompatibilityException("unreadable_native_method_pointer");
        return value;
    }

    private static bool IsReadablePointer(IntPtr address)
    {
        if (!OperatingSystem.IsWindows() || address == IntPtr.Zero)
            return false;
        var size = (nuint)Marshal.SizeOf<MemoryInformation>();
        if (
            VirtualQuery(address, out var region, size) != size
            || region.State != 0x1000
            || (region.Protect & 0x100) != 0
            || (region.Protect & 0xff) is not (0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80)
        )
            return false;
        var start = (nuint)address;
        var begin = (nuint)region.BaseAddress;
        return start >= begin
            && region.RegionSize >= (nuint)IntPtr.Size
            && start - begin <= region.RegionSize - (nuint)IntPtr.Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        internal IntPtr BaseAddress;
        internal IntPtr AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal nuint RegionSize;
        internal uint State;
        internal uint Protect;
        internal uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(
        IntPtr address,
        out MemoryInformation region,
        nuint size
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr address,
        out IntPtr value,
        nuint size,
        out nuint read
    );

    internal void RequireValidated(MethodBase method)
    {
        long rva;
        lock (gate)
            if (!validated.TryGetValue(method, out rva))
                throw new RuntimeCompatibilityException("uncertified_hook");
        RequireUniqueNativeBody(rva);
    }

    internal static void RequireUniqueNativeBody(long rva) => NativeHookBodies.RequireUnique(rva);
}
