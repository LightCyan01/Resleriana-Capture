using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtelierResleriana.Unity;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AtelierCapture.Checks;

internal static class LocalizationChecks
{
    private const string OriginalHash =
        "0b590b78f82cf50f5c3950a447d3f19e1333b2f8c76c8ee662c15104b5f4fec0";
    private const string CorrectedHash =
        "a53a51c19f2c4a0a1c8d26a181d49c09f3e524b583f96641b1294768ccd494cf";
    private const string PluginPath = "BepInEx/plugins/AtelierResleriana.Plugin.Localization.dll";

    internal static void Run(string root, string? companionPath = null)
    {
        var output = Path.Combine(root, "work/checks/localization-fourth");
        Directory.CreateDirectory(output);
        using var archive = ZipFile.OpenRead(
            Path.Combine(root, "work/build/dependencies/localization.zip")
        );
        using var source = archive.GetEntry(PluginPath)!.Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        var original = bytes.ToArray();
        Check(Hash(original) == OriginalHash, "pinned public companion input");
        var corrected = PatchWithBuildScript(root, output);
        Check(Hash(corrected) == CorrectedHash, "pinned corrected companion output");
        companionPath ??= Path.Combine(root, "work/build/stage", PluginPath);
        Check(
            Hash(File.ReadAllBytes(companionPath)) == CorrectedHash,
            "verified companion artifact must contain the PF17 correction"
        );
        Check(original.Length == corrected.Length, "unchanged PE length");
        var differences = Enumerable
            .Range(0, original.Length)
            .Where(i => original[i] != corrected[i])
            .ToArray();
        Check(
            differences.SequenceEqual(new[] { 0x107B }),
            "only the approved operand byte changed"
        );
        using var before = AssemblyDefinition.ReadAssembly(new MemoryStream(original));
        using var after = AssemblyDefinition.ReadAssembly(new MemoryStream(corrected));
        var oldMethod = Method(before);
        var newMethod = Method(after);
        Check(oldMethod.RVA == 0x2C70 && newMethod.RVA == oldMethod.RVA, "method RVA");
        Check(oldMethod.MetadataToken.ToUInt32() == 0x06000041, "method token");
        Check(
            oldMethod.Body.CodeSize == 1345 && newMethod.Body.CodeSize == 1345,
            "unchanged IL extent"
        );
        Check(
            before.Name.Version == new Version(2, 0, 0, 0)
                && before.Name.FullName == after.Name.FullName,
            "companion assembly identity"
        );
        Check(before.MainModule.Mvid == after.MainModule.Mvid, "unchanged module identity");
        Check(
            before
                .MainModule.AssemblyReferences.Select(r => r.FullName)
                .SequenceEqual(after.MainModule.AssemblyReferences.Select(r => r.FullName)),
            "unchanged assembly references"
        );
        Check(Regions(oldMethod).SequenceEqual(Regions(newMethod)), "unchanged EH table");
        Check(
            Membership(oldMethod, 0x1FE) == Membership(oldMethod, 0x440)
                && Membership(oldMethod, 0x440) == Membership(oldMethod, 0x470),
            "branch targets remain in the same EH regions"
        );
        var oldBranch = At(oldMethod, 0x1FE);
        var newBranch = At(newMethod, 0x1FE);
        Check(
            oldBranch.OpCode.Code == Code.Brfalse && newBranch.OpCode.Code == Code.Brfalse,
            "unchanged conditional opcode"
        );
        Check(
            ((Instruction)oldBranch.Operand).Offset == 0x470
                && ((Instruction)newBranch.Operand).Offset == 0x440,
            "only branch destination corrected"
        );
        Check(
            ((MethodReference)At(oldMethod, 0x1F9).Operand).FullName
                == "System.Boolean AtelierResleriana.Plugin.Localization.LocalizationService::IsWhitelistedSerializedFile(System.String)",
            "whitelist produces the consumed condition"
        );
        Check(
            ExitDecision(newMethod, 0x17E, 5) == 0x540,
            "no whitelisted CAB keeps original input"
        );
        Check(
            ExitDecision(newMethod, 0x481, 8) == 0x4F6,
            "no translated object keeps original input"
        );
        var modifiedSerialized = SerializedPreservation();
        DirectoryPreservation(oldMethod, newMethod, modifiedSerialized);
        Console.WriteLine(
            "Localization PF17 PASS: exact byte patch, actual IL copy block, mixed CABs, UnityFS and synthetic serialized references."
        );
    }

    private static byte[] PatchWithBuildScript(string root, string output)
    {
        var correctedPath = Path.Combine(output, "AtelierResleriana.Plugin.Localization.dll");
        var start = new ProcessStartInfo("py")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = output,
        };
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        foreach (
            var argument in new[]
            {
                "-",
                Path.Combine(root, "scripts"),
                Path.Combine(root, "work/build/dependencies/localization.zip"),
                correctedPath,
            }
        )
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Write(
            """
            import hashlib, pathlib, sys, zipfile
            sys.path.insert(0, sys.argv[1])
            import build
            with zipfile.ZipFile(sys.argv[2]) as archive:
                original = archive.read(build.LOCALIZATION_PATCH['path'])
            corrected = build.patch_localization(original)
            assert corrected == build.patch_localization(original)
            for bad in (original[:-1], b'foreign input', corrected,
                        original[:0x200] + bytes([original[0x200] ^ 1]) + original[0x201:],
                        original[:0x107b] + bytes([original[0x107b] ^ 1]) + original[0x107c:]):
                try:
                    build.patch_localization(bad)
                except ValueError:
                    continue
                raise AssertionError('Unexpected or already-fixed dependency accepted')
            pathlib.Path(sys.argv[3]).write_bytes(corrected)
            print('Deterministic PF17 patch and five fail-closed input controls PASS')
            """
        );
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new TimeoutException("Private localization patch control timed out");
        }
        Check(
            process.ExitCode == 0,
            stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()
        );
        File.WriteAllText(
            Path.Combine(output, "patch-controls.log"),
            stdout.GetAwaiter().GetResult()
        );
        return File.ReadAllBytes(correctedPath);
    }

    private static MethodDefinition Method(AssemblyDefinition assembly) =>
        assembly
            .MainModule.GetType("AtelierResleriana.Plugin.Localization.Plugin")
            .Methods.Single(m => m.Name == "AssetBundleLoadFromStreamPrefix");

    private static Instruction At(MethodDefinition method, int offset) =>
        method.Body.Instructions.Single(i => i.Offset == offset);

    private static string[] Regions(MethodDefinition method) =>
        method
            .Body.ExceptionHandlers.Select(e =>
                $"{e.HandlerType}:{e.TryStart.Offset}:{e.TryEnd.Offset}:{e.HandlerStart.Offset}:{e.HandlerEnd.Offset}:{e.CatchType?.FullName}"
            )
            .ToArray();

    private static string Membership(MethodDefinition method, int offset) =>
        string.Join(
            ",",
            method.Body.ExceptionHandlers.Select(
                (e, i) =>
                    offset >= e.TryStart.Offset && offset < e.TryEnd.Offset ? $"try{i}"
                    : offset >= e.HandlerStart.Offset && offset < e.HandlerEnd.Offset
                        ? $"handler{i}"
                    : ""
            )
        );

    private static int ExitDecision(MethodDefinition method, int loadOffset, int local)
    {
        Check(
            At(method, loadOffset).OpCode.Code == Code.Ldloc_S
                && ((VariableDefinition)At(method, loadOffset).Operand).Index == local,
            "original no-change flag load"
        );
        var branch = At(method, loadOffset).Next;
        Check(branch.OpCode.FlowControl == FlowControl.Cond_Branch, "original no-change decision");
        return branch.OpCode.Code switch
        {
            Code.Brfalse_S => ((Instruction)branch.Operand).Offset,
            Code.Brtrue_S when branch.Next.OpCode.Code == Code.Leave => (
                (Instruction)branch.Next.Operand
            ).Offset,
            _ => throw new InvalidOperationException("Unexpected no-change IL"),
        };
    }

    private static void DirectoryPreservation(
        MethodDefinition oldMethod,
        MethodDefinition newMethod,
        byte[] modifiedSerialized
    )
    {
        var originalDirectories = new[]
        {
            new UnityFSFileDirectory
            {
                Path = "CAB-known",
                Flags = 4,
                Bytes = Encoding.UTF8.GetBytes("synthetic prior text"),
            },
            new UnityFSFileDirectory
            {
                Path = "CAB-unknown",
                Flags = 4,
                Bytes = new byte[] { 0, 255, 3, 9, 0 },
            },
            new UnityFSFileDirectory
            {
                Path = "CAB-blacklisted",
                Flags = 8,
                Bytes = Enumerable.Range(0, 129).Select(i => (byte)i).ToArray(),
            },
            new UnityFSFileDirectory
            {
                Path = "CAB-known.resS",
                Flags = 0,
                Bytes = Enumerable.Range(0, 8193).Select(i => (byte)(i * 17)).ToArray(),
            },
        };
        var original = ReadBundle(WriteBundle(originalDirectories));
        List<UnityFSFileDirectory> Interpret(MethodDefinition method)
        {
            var result = new List<UnityFSFileDirectory>
            {
                new()
                {
                    Path = "CAB-known",
                    Flags = 4,
                    Bytes = modifiedSerialized,
                },
            };
            foreach (var directory in original.Metadata.DirectoryInfos.Skip(1))
                CopyDirectory(
                    method,
                    original,
                    directory,
                    result,
                    directory.Path.EndsWith(".resS", StringComparison.Ordinal)
                );
            return result;
        }
        var oldResult = ReadBundle(WriteBundle(Interpret(oldMethod)));
        Check(
            oldResult
                .Metadata.DirectoryInfos.Select(d => d.Path)
                .SequenceEqual(new[] { "CAB-known", "CAB-known.resS" }),
            "original IL loses unknown and blacklisted CABs: red control"
        );
        var fixedBytes = WriteBundle(Interpret(newMethod));
        var fixedResult = ReadBundle(fixedBytes);
        Check(
            fixedResult
                .Metadata.DirectoryInfos.Select(d => d.Path)
                .SequenceEqual(originalDirectories.Select(d => d.Path)),
            "fixed IL preserves all directory names and order"
        );
        long offset = 0;
        for (var i = 0; i < originalDirectories.Length; i++)
        {
            var directory = fixedResult.Metadata.DirectoryInfos[i];
            var expected = i == 0 ? modifiedSerialized : originalDirectories[i].Bytes;
            Check(
                directory.Flags == originalDirectories[i].Flags
                    && directory.Offset == offset
                    && directory.Size == expected.Length,
                "directory flags, bounds and rebuilt offsets"
            );
            Check(
                fixedResult.GetDirectoryBytes(directory).AsSpan().SequenceEqual(expected),
                "localized bytes or exact untouched payload"
            );
            offset += directory.Size;
        }
        Check(offset == fixedResult.Data.Length, "all uncompressed directory bytes accounted for");
        Check(
            WriteBundle(Interpret(newMethod)).AsSpan().SequenceEqual(fixedBytes),
            "stable source/locale repeat has deterministic complete bytes"
        );
    }

    private static void CopyDirectory(
        MethodDefinition method,
        UnityFSFile file,
        UnityFSFileDirectoryInfo directory,
        List<UnityFSFileDirectory> output,
        bool nonCab
    )
    {
        var locals = new Dictionary<int, object?>
        {
            [7] = file,
            [9] = output,
            [16] = directory,
            [17] = directory.Path,
            [18] = false,
        };
        var stack = new Stack<object?>();
        var instruction = At(method, nonCab ? 0x43C : 0x1FE);
        if (!nonCab)
            stack.Push(false);
        for (var steps = 0; instruction.Offset != 0x470; steps++)
        {
            Check(steps < 40, "bounded actual IL copy block");
            switch (instruction.OpCode.Code)
            {
                case Code.Ldloc_S:
                    stack.Push(locals[((VariableDefinition)instruction.Operand).Index]);
                    break;
                case Code.Brfalse:
                case Code.Brtrue_S:
                    var condition = (bool)stack.Pop()!;
                    if (condition == (instruction.OpCode.Code == Code.Brtrue_S))
                    {
                        instruction = (Instruction)instruction.Operand;
                        continue;
                    }
                    break;
                case Code.Dup:
                    stack.Push(stack.Peek());
                    break;
                case Code.Newobj:
                case Code.Callvirt:
                    var member = RestrictedMember((MethodReference)instruction.Operand);
                    var arguments = new object?[member.GetParameters().Length];
                    for (var i = arguments.Length - 1; i >= 0; i--)
                        arguments[i] = stack.Pop();
                    if (member is ConstructorInfo constructor)
                        stack.Push(constructor.Invoke(arguments));
                    else
                    {
                        var call = (MethodInfo)member;
                        var value = call.Invoke(stack.Pop(), arguments);
                        if (call.ReturnType != typeof(void))
                            stack.Push(value);
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unqualified directory-copy IL: " + instruction
                    );
            }
            instruction = instruction.Next;
        }
        Check(stack.Count == 0, "original and corrected branch/copy targets have an empty stack");
    }

    private static MethodBase RestrictedMember(MethodReference member) =>
        member.FullName switch
        {
            "System.Void AtelierResleriana.Unity.UnityFSFileDirectory::.ctor()" =>
                typeof(UnityFSFileDirectory).GetConstructor(Type.EmptyTypes)!,
            "System.Void AtelierResleriana.Unity.UnityFSFileDirectory::set_Path(System.String)" =>
                typeof(UnityFSFileDirectory).GetProperty("Path")!.SetMethod!,
            "System.Void AtelierResleriana.Unity.UnityFSFileDirectory::set_Bytes(System.Byte[])" =>
                typeof(UnityFSFileDirectory).GetProperty("Bytes")!.SetMethod!,
            "System.Void AtelierResleriana.Unity.UnityFSFileDirectory::set_Flags(System.UInt32)" =>
                typeof(UnityFSFileDirectory).GetProperty("Flags")!.SetMethod!,
            "System.UInt32 AtelierResleriana.Unity.UnityFSFileDirectoryInfo::get_Flags()" =>
                typeof(UnityFSFileDirectoryInfo).GetProperty("Flags")!.GetMethod!,
            "System.Byte[] AtelierResleriana.Unity.UnityFSFile::GetDirectoryBytes(AtelierResleriana.Unity.UnityFSFileDirectoryInfo)" =>
                typeof(UnityFSFile).GetMethod(
                    "GetDirectoryBytes",
                    [typeof(UnityFSFileDirectoryInfo)]
                )!,
            "System.Void System.Collections.Generic.ICollection`1<AtelierResleriana.Unity.UnityFSFileDirectory>::Add(!0)" =>
                typeof(ICollection<UnityFSFileDirectory>).GetMethod("Add")!,
            _ => throw new InvalidOperationException("Unqualified IL member: " + member.FullName),
        };

    private static byte[] SerializedPreservation()
    {
        void SetLevel(SerializedFileTypeTreeNode node, int level)
        {
            node.Level = level;
            foreach (var child in node.Children)
                SetLevel(child, level + 1);
        }
        SerializedFileTypeTreeNode Node(
            string type,
            string name,
            int size = -1,
            params SerializedFileTypeTreeNode[] children
        )
        {
            var node = new SerializedFileTypeTreeNode
            {
                Type = type,
                Name = name,
                Size = size,
                Version = 1,
                MetaFlag = 0,
                Children = children,
            };
            SetLevel(node, 0);
            return node;
        }
        SerializedFileTypeTreeNode Pointer(string type, string name) =>
            Node(type, name, -1, Node("int", "m_FileID", 4), Node("SInt64", "m_PathID", 8));
        var types = new[]
        {
            new SerializedFileType
            {
                ClassId = 49,
                IsStrippedType = false,
                ScriptTypeIndex = -1,
                OldTypeHash = new byte[16],
                Node = Node(
                    "TextAsset",
                    "Base",
                    -1,
                    Node("string", "m_Name"),
                    Node("TypelessData", "m_Script")
                ),
            },
            new SerializedFileType
            {
                ClassId = 21,
                IsStrippedType = false,
                ScriptTypeIndex = -1,
                OldTypeHash = new byte[16],
                Node = Node(
                    "Material",
                    "Base",
                    -1,
                    Node("string", "m_Name"),
                    Pointer("PPtr<Shader>", "m_Shader"),
                    Node("TypelessData", "m_Opaque")
                ),
            },
            new SerializedFileType
            {
                ClassId = 114,
                IsStrippedType = false,
                ScriptTypeIndex = 0,
                ScriptId = Enumerable.Repeat((byte)0x5A, 16).ToArray(),
                OldTypeHash = new byte[16],
                Node = Node(
                    "MonoBehaviour",
                    "Base",
                    -1,
                    Pointer("PPtr<MonoScript>", "m_Script"),
                    Node("int", "m_Enabled", 4)
                ),
            },
        };
        Dictionary<string, object> Reference(int file, long path) =>
            new() { ["m_FileID"] = file, ["m_PathID"] = path };
        var objects = new[]
        {
            new SerializedFileWriter.SerializedFileObject
            {
                PathId = 101,
                TypeId = 0,
                DataIndex = 0,
                SerializedObject = new SerializedObject(
                    49,
                    new()
                    {
                        ["m_Name"] = Encoding.UTF8.GetBytes("SyntheticText"),
                        ["m_Script"] = Encoding.UTF8.GetBytes("prior synthetic text"),
                    }
                ),
            },
            new SerializedFileWriter.SerializedFileObject
            {
                PathId = 202,
                TypeId = 1,
                DataIndex = 1,
                SerializedObject = new SerializedObject(
                    21,
                    new()
                    {
                        ["m_Name"] = Encoding.UTF8.GetBytes("synthetic material"),
                        ["m_Shader"] = Reference(1, 0x100000007),
                        ["m_Opaque"] = new byte[] { 1, 0, 255, 7, 3 },
                    }
                ),
            },
            new SerializedFileWriter.SerializedFileObject
            {
                PathId = 303,
                TypeId = 2,
                DataIndex = 2,
                SerializedObject = new SerializedObject(
                    114,
                    new() { ["m_Script"] = Reference(-1, 0x200000009), ["m_Enabled"] = 1 }
                ),
            },
        };
        var scripts = new[]
        {
            new SerializedFileObjectReference { Index = 1, PathId = 0x30000000B },
        };
        var assets = new[]
        {
            new SerializedFileAssetReference
            {
                Path = "archive:/CAB-external/CAB-external",
                Guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
                Type = 0,
            },
        };
        var referenceTypes = new[]
        {
            new SerializedFileType
            {
                ClassId = 114,
                IsStrippedType = false,
                ScriptTypeIndex = 0,
                ScriptId = new byte[16],
                OldTypeHash = new byte[16],
                Node = Node("SyntheticReference", "Base", -1, Node("int", "value", 4)),
                ClassName = "SyntheticReference",
                Namespace = "Checks",
                AssemblyName = "Synthetic",
            },
        };
        var writer = new SerializedFileWriter(
            new()
            {
                IsBigEndian = false,
                UnityVersion = "2022.3.62f2",
                TargetPlatform = 19,
                EnableTypeTree = true,
            }
        );
        using var originalStream = writer.Write(
            types,
            objects,
            scripts,
            assets,
            referenceTypes,
            "synthetic-only"
        );
        var original = new SerializedFileReader().Read(originalStream);
        var replacement = original
            .Objects.Select(
                (item, index) =>
                    new SerializedFileWriter.SerializedFileObject
                    {
                        PathId = item.PathId,
                        TypeId = item.TypeId,
                        DataIndex = index,
                        SerializedObject = original.GetSerializedObject(item),
                    }
            )
            .ToArray();
        replacement[0].SerializedObject["m_Script"] = Encoding.UTF8.GetBytes(
            "English synthetic translation with a different size"
        );
        using var modified = writer.Write(
            original.Types,
            replacement,
            original.ScriptReferences,
            original.AssetReferences,
            original.ReferenceTypes,
            original.UserInformation!
        );
        using var copy = new MemoryStream();
        modified.CopyTo(copy);
        modified.Position = 0;
        var roundtrip = new SerializedFileReader().Read(modified);
        Check(
            original
                .Objects.Select(o => (o.PathId, o.TypeId))
                .SequenceEqual(roundtrip.Objects.Select(o => (o.PathId, o.TypeId))),
            "all synthetic path/type IDs preserved"
        );
        for (var i = 1; i < original.Objects.Length; i++)
            Check(
                original.Objects[i].Data.AsSpan().SequenceEqual(roundtrip.Objects[i].Data),
                "non-text material/shader or script pointer payload preserved"
            );
        Check(
            JsonSerializer.Serialize(original.Types) == JsonSerializer.Serialize(roundtrip.Types),
            "type trees and script type identifiers preserved"
        );
        Check(
            JsonSerializer.Serialize(original.ScriptReferences)
                == JsonSerializer.Serialize(roundtrip.ScriptReferences),
            "script reference table preserved"
        );
        Check(
            JsonSerializer.Serialize(original.AssetReferences)
                == JsonSerializer.Serialize(roundtrip.AssetReferences),
            "external references preserved"
        );
        Check(
            JsonSerializer.Serialize(original.ReferenceTypes)
                == JsonSerializer.Serialize(roundtrip.ReferenceTypes),
            "reference types preserved"
        );
        Check(
            original.UserInformation == roundtrip.UserInformation,
            "serialized user metadata preserved"
        );
        Check(
            ((byte[])roundtrip.GetSerializedObject(roundtrip.Objects[0])["m_Script"])
                .AsSpan()
                .SequenceEqual((byte[])replacement[0].SerializedObject["m_Script"]),
            "only qualified synthetic text payload replaced"
        );
        return copy.ToArray();
    }

    private static byte[] WriteBundle(IEnumerable<UnityFSFileDirectory> directories)
    {
        using var stream = new UnityFSFileWriter(
            new() { Compression = UnityFSFileCompression.Lz4 }
        ).Write(directories);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static UnityFSFile ReadBundle(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new UnityFSFileReader().Read(stream);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Localization PF17: " + message);
    }
}
