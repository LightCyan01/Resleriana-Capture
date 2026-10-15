using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Hook;
using BepInEx.Unity.IL2CPP.Logging;
using Il2CppInterop.Common;

namespace AtelierCapture.Checks;

internal static class BuildChecks
{
    internal static void Run()
    {
        var root = FindRoot();
        var runtime = root + "/work/build/stage/BepInEx/core";
        var interop = Assembly.LoadFrom(Path.Combine(runtime, "Il2CppInterop.Runtime.dll"));
        var scanType = interop.GetType("Il2CppInterop.Runtime.MemoryUtils", true)!;
        var scan = scanType.GetMethod(
            "FindSignatureInBlock",
            [typeof(nint), typeof(long), typeof(char[]), typeof(char[]), typeof(long)]
        )!;
        var memory = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new byte[] { 9, 7, 2, 4, 6, 8 }, 0, memory, 6);
            Check(
                (nint)
                    scan.Invoke(
                        null,
                        [memory, 6L, new[] { (char)6, (char)8 }, new[] { 'x', 'x' }, 0L]
                    )!
                    == memory + 4,
                "last complete match"
            );
            Check(
                (nint)
                    scan.Invoke(
                        null,
                        [memory, 1L, new[] { (char)7, (char)2 }, new[] { 'x', 'x' }, 0L]
                    )! == 0,
                "short block"
            );
            Throws<ArgumentException>(() =>
                scan.Invoke(null, [memory, 6L, new[] { (char)6 }, new[] { '!' }, 0L])
            );
            Throws<ArgumentException>(() =>
                scan.Invoke(null, [memory, 6L, Array.Empty<char>(), Array.Empty<char>(), 0L])
            );
            Throws<ArgumentOutOfRangeException>(() =>
                scan.Invoke(null, [memory, 6L, new[] { (char)9 }, new[] { 'x' }, -1L])
            );
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
        GuardedScanner(scan);
        ReadableRangeScanner(scanType);

        var support = Assembly.LoadFrom(Path.Combine(runtime, "Il2CppInterop.HarmonySupport.dll"));
        var valueType = support.GetType("Il2CppInterop.HarmonySupport.NativeValueStorage", true)!;
        var inline = valueType.GetMethod(
            "IsInlineWindows64Value",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        foreach (var size in new[] { 1, 2, 4, 8 })
            Check((bool)inline.Invoke(null, [size])!, "Windows register aggregate");
        foreach (var size in new[] { 0, 3, 5, 16 })
            Check(!(bool)inline.Invoke(null, [size])!, "indirect aggregate");
        var copy = valueType.GetMethod(
            "CopyNullablePayload",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        var payload = Marshal.AllocHGlobal(4);
        var box = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(payload, 0x1234567);
            copy.Invoke(null, [box, 32, (nint)0x1230, payload, 4]);
            Check(
                Marshal.ReadIntPtr(box) == 0x1230 && Marshal.ReadInt32(box + 16) == 0x1234567,
                "nullable payload"
            );
            for (var offset = 20; offset < 32; offset++)
                Check(
                    Marshal.ReadByte(box + offset) == 0,
                    "object padding must not read caller padding"
                );
            copy.Invoke(null, [box, 32, (nint)0x1230, (nint)0, 4]);
            Check(Marshal.ReadInt32(box + 16) == 0, "empty nullable storage");
            Throws<NotSupportedException>(() =>
                copy.Invoke(null, [box, 16, (nint)0x1230, payload, 4])
            );
            Throws<NotSupportedException>(() =>
                copy.Invoke(null, [box, 32, (nint)0x1230, payload, 17])
            );
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
            Marshal.FreeHGlobal(box);
        }
        Lifecycle();
        ManagedProxyLifecycle();
        NativeLogCallback();
        PriorityLogger();
        ConsoleSuppression();
        GeneratedPointerFields();
        LocalizationConfiguration(root);
        LocalizationChecks.Run(root);
        SourceAndRuntimeContracts(root);
        NativeObserverBoundary();
        NativeForwarding(root);
        NativeRaceChildren(root);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong NativeFixture(
        nint callback,
        nint prepare,
        nint commit,
        nint destroy,
        uint race
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ScrambleThreadState();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeFixtureObserver(nint frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NativeCatalog();

    private static unsafe void NativeForwarding(string root)
    {
        var path = Path.Combine(root, "work/build/native");
        var staged = Path.Combine(
            root,
            "work/build/stage/BepInEx/plugins/AtelierCapture.Native.dll"
        );
        Check(
            File.Exists(staged)
                && SHA256
                    .HashData(File.ReadAllBytes(staged))
                    .AsSpan()
                    .SequenceEqual(
                        SHA256.HashData(
                            File.ReadAllBytes(Path.Combine(path, "AtelierCapture.Native.dll"))
                        )
                    ),
            "native fixture uses exact packaged observer bytes"
        );
        var bridge = NativeLibrary.Load(Path.Combine(path, "AtelierCapture.Native.dll"));
        NativeLibrary.Load(Path.Combine(path, "AtelierCapture.Native.Thrower.dll"));
        var fixture = NativeLibrary.Load(Path.Combine(path, "AtelierCapture.Native.Checks.dll"));
        var scramble = Marshal.GetDelegateForFunctionPointer<ScrambleThreadState>(
            NativeLibrary.GetExport(fixture, "fixture_scramble_thread_state")
        );
        var dobby = NativeLibrary.Load(
            Path.Combine(root, "work/build/stage/BepInEx/core/dobby.dll")
        );
        var catalogFunction = Marshal.GetDelegateForFunctionPointer<NativeCatalog>(
            NativeLibrary.GetExport(bridge, "capture_catalog")
        );
        using var catalog = JsonDocument.Parse(Marshal.PtrToStringUTF8(catalogFunction())!);
        var contracts = catalog
            .RootElement.GetProperty("contracts")
            .EnumerateArray()
            .Select(r => r.GetProperty("identity").GetString()!)
            .ToArray();
        var phases = new long[4];
        long handles = 0,
            failures = 0,
            controlledFailures = 0;
        void Callback(nint pointer)
        {
            ref var frame = ref Unsafe.AsRef<NativeObservationFrame>((void*)pointer);
            try
            {
                Check(
                    frame.Size == 64
                        && frame.Version == 1
                        && frame.Binding < (ulong)contracts.Length,
                    "native frame header"
                );
                Interlocked.Increment(ref phases[(int)frame.Phase]);
                if (frame.Phase == NativeObservationPhase.Entering)
                {
                    Check(
                        frame.Token == 0 && frame.Result == 0,
                        "native entering has no result/token"
                    );
                    var invocation = new NativeFixtureState(
                        frame.Receiver == 0 ? 0 : Marshal.ReadInt64(frame.Receiver)
                    );
                    frame.Token = GCHandle.ToIntPtr(GCHandle.Alloc(invocation));
                    Interlocked.Increment(ref handles);
                    if (frame.Binding % 19 == 0)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
                else
                {
                    Check(
                        frame.Token != 0
                            && GCHandle.FromIntPtr(frame.Token).Target is NativeFixtureState,
                        "native invocation token survives callback return/GC"
                    );
                    var invocation = (NativeFixtureState)GCHandle.FromIntPtr(frame.Token).Target!;
                    if (frame.Phase == NativeObservationPhase.Leaving)
                    {
                        Check(
                            frame.Result == 0 && invocation.Terminal == 1,
                            "native terminal once before leaving without borrowed result"
                        );
                        GCHandle.FromIntPtr(frame.Token).Free();
                        frame.Token = 0;
                        Interlocked.Decrement(ref handles);
                    }
                    else
                    {
                        invocation.Terminal++;
                        if (frame.Phase == NativeObservationPhase.Faulted)
                            Check(
                                frame.Result == 0,
                                "native fault exposes no uninitialized result"
                            );
                        if (frame.Receiver != 0)
                            Check(
                                Marshal.ReadInt64(frame.Receiver) >= invocation.Before,
                                "native original state remains visible to phase observer"
                            );
                    }
                }
                if (frame.Binding % 23 == 0)
                {
                    try
                    {
                        throw new InvalidOperationException("controlled observer failure");
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref controlledFailures);
                    }
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failures);
            }
            scramble();
        }
        NativeFixtureObserver observer = Callback;
        var run = Marshal.GetDelegateForFunctionPointer<NativeFixture>(
            NativeLibrary.GetExport(fixture, "fixture_run")
        );
        var assertions = run(
            Marshal.GetFunctionPointerForDelegate(observer),
            NativeLibrary.GetExport(dobby, "DobbyPrepare"),
            NativeLibrary.GetExport(dobby, "DobbyCommit"),
            NativeLibrary.GetExport(dobby, "DobbyDestroy"),
            0
        );
        GC.KeepAlive(observer);
        Check(
            assertions > 3000 && phases[0] >= 460 && phases[2] == contracts.Length,
            "native finite forwarding and real C++ exception identity controls"
        );
        Check(
            phases[0] == phases[1] + phases[2] && phases[0] == phases[3],
            "native phases close exactly once"
        );
        Check(
            handles == 0 && failures == 0 && controlledFailures > 0,
            "native callback failures contain and release rooted invocation tokens"
        );
        Console.WriteLine(
            $"Native bridge: {assertions} native assertions; {contracts.Length} finite ABI slots; {phases[0]} invocations; tokens=0."
        );
    }

    private sealed class NativeFixtureState(long before)
    {
        internal readonly long Before = before;
        internal int Terminal;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void SetLastError(uint error);

    internal static void RunNativeRace(uint race)
    {
        var root = FindRoot();
        var path = Path.Combine(root, "work/build/native");
        NativeLibrary.Load(Path.Combine(path, "AtelierCapture.Native.Races.dll"));
        NativeLibrary.Load(Path.Combine(path, "AtelierCapture.Native.Thrower.dll"));
        var fixture = NativeLibrary.Load(
            Path.Combine(path, "AtelierCapture.Native.RaceChecks.dll")
        );
        var dobby = NativeLibrary.Load(
            Path.Combine(root, "work/build/stage/BepInEx/core/dobby.dll")
        );
        NativeFixtureObserver observer = _ => { };
        var run = Marshal.GetDelegateForFunctionPointer<NativeFixture>(
            NativeLibrary.GetExport(fixture, "fixture_run")
        );
        var assertions = run(
            Marshal.GetFunctionPointerForDelegate(observer),
            NativeLibrary.GetExport(dobby, "DobbyPrepare"),
            NativeLibrary.GetExport(dobby, "DobbyCommit"),
            NativeLibrary.GetExport(dobby, "DobbyDestroy"),
            race
        );
        GC.KeepAlive(observer);
        Check(assertions > 4000, "native deterministic close race " + race);
        Console.WriteLine($"Native race {race}: {assertions} native assertions.");
    }

    private static void NativeRaceChildren(string root)
    {
        var executable = Environment.ProcessPath!;
        foreach (var race in new[] { 1, 2, 3 })
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (
                Path.GetFileNameWithoutExtension(executable)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            )
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--native-race-child");
            start.ArgumentList.Add(race.ToString());
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30000))
            {
                child.Kill();
                throw new TimeoutException("Native deterministic race did not complete.");
            }
            var log = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.Combine(root, "work/checks/native"));
            File.WriteAllText(Path.Combine(root, $"work/checks/native/race-{race}.log"), log);
            Check(
                child.ExitCode == 0
                    && log.Contains($"Native race {race}:", StringComparison.Ordinal),
                "native deterministic race child " + race
            );
        }
    }

    private static int PrimitiveNativeFixture(int value) => value;

    private static void NativeObserverBoundary()
    {
        var memory = Marshal.AllocHGlobal(256);
        try
        {
            var target = typeof(BuildChecks).GetMethod(
                nameof(PrimitiveNativeFixture),
                BindingFlags.Static | BindingFlags.NonPublic
            )!;
            var constructor = typeof(PassiveNativeHook)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single();
            var observe = typeof(PassiveNativeHook).GetMethod(
                "Observe",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var retained = memory + 80;
            var result = memory + 88;
            var argumentCells = memory + 96;
            Marshal.WriteInt32(retained, -1234);
            Marshal.WriteInt32(result, 4321);
            Marshal.WriteIntPtr(argumentCells, retained);
            for (var mode = 0; mode < 4; mode++)
            {
                var selected = mode;
                long enters = 0,
                    returns = 0,
                    faults = 0,
                    leaves = 0,
                    failures = 0;
                NativeObservation? expired = null;
                Func<NativeObservation, object?> enter = frame =>
                {
                    enters++;
                    expired = frame;
                    Check(
                        frame.Materialize().Arguments is [-1234],
                        "actual bridge primitive entry materialization"
                    );
                    if (selected == 0)
                        throw new InvalidOperationException("controlled enter failure");
                    return "rooted state";
                };
                Action<NativeObservation, object?> returned = (frame, state) =>
                {
                    returns++;
                    Check(
                        (int)frame.Materialize().Result! == 4321 && Equals(state, "rooted state"),
                        "actual bridge returned state/result"
                    );
                    if (selected == 1)
                        throw new InvalidOperationException("controlled returned failure");
                };
                Action<NativeObservation, object?> faulted = (frame, state) =>
                {
                    faults++;
                    Check(
                        frame.Materialize().Result is null && Equals(state, "rooted state"),
                        "actual bridge fault has no result"
                    );
                    if (selected == 2)
                        throw new InvalidOperationException("controlled fault failure");
                };
                Action<object?> leave = _ =>
                {
                    leaves++;
                    if (selected == 3)
                        throw new InvalidOperationException("controlled leave failure");
                };
                Action<Exception> fail = _ =>
                {
                    failures++;
                    throw new InvalidOperationException("controlled reporter failure");
                };
                var hook = constructor.Invoke([
                    target,
                    0,
                    (nint)123,
                    new[] { "i4" },
                    "i4",
                    enter,
                    returned,
                    faulted,
                    leave,
                    fail,
                ]);
                var frame = new NativeObservationFrame
                {
                    Size = 64,
                    Version = 1,
                    Binding = 0,
                    ArgumentCount = 1,
                    Arguments = argumentCells,
                };
                Marshal.StructureToPtr(frame, memory, false);
                observe.Invoke(hook, [memory]);
                frame = Marshal.PtrToStructure<NativeObservationFrame>(memory);
                Check(frame.Token != 0, "actual bridge allocates token before observer");
                Throws<InvalidOperationException>(() => expired!.Materialize());
                if (selected != 0)
                {
                    frame.Phase =
                        selected == 2
                            ? NativeObservationPhase.Faulted
                            : NativeObservationPhase.Returned;
                    frame.Result = selected == 2 ? 0 : result;
                    Marshal.StructureToPtr(frame, memory, false);
                    observe.Invoke(hook, [memory]);
                    frame = Marshal.PtrToStructure<NativeObservationFrame>(memory);
                }
                frame.Phase = NativeObservationPhase.Leaving;
                frame.Result = 0;
                Marshal.StructureToPtr(frame, memory, false);
                observe.Invoke(hook, [memory]);
                frame = Marshal.PtrToStructure<NativeObservationFrame>(memory);
                Check(
                    frame.Token == 0 && enters == 1 && leaves == 1 && failures == 1,
                    "actual bridge contains failure and frees invocation token once"
                );
                observe.Invoke(hook, [memory]);
                Check(leaves == 1, "actual bridge repeated leave does not repeat owner cleanup");
                Check(
                    returns == (selected is 1 or 3 ? 1 : 0) && faults == (selected == 2 ? 1 : 0),
                    "actual bridge selected terminal phase"
                );
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static void GeneratedPointerFields()
    {
        foreach (var type in new[] { typeof(PointerFixture), typeof(PointerFixture<int>) })
        {
            var constructor = type.GetConstructor(Type.EmptyTypes)!;
            var constructorField =
                Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(constructor);
            Check(
                constructorField.Name == "NativeMethodInfoPtr_Constructor"
                    && constructorField.DeclaringType == type
                    && (nint)constructorField.GetValue(null)! == 0x1234,
                "constructor exact pointer field"
            );
            var method = type.GetMethod("Method")!;
            var methodField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(
                method
            );
            Check(
                methodField.Name == "NativeMethodInfoPtr_Method"
                    && methodField.DeclaringType == type
                    && (nint)methodField.GetValue(null)! == 0x5678,
                "method exact pointer field"
            );
            var accessor = type.GetMethod("FieldAccessor")!;
            var field = Il2CppInteropUtils.GetIl2CppFieldInfoPointerFieldForGeneratedFieldAccessor(
                accessor
            );
            Check(
                field.Name == "NativeFieldInfoPtr_Value"
                    && field.DeclaringType == type
                    && (nint)field.GetValue(null)! == 0x9876,
                "accessor exact pointer field"
            );
            var generic = type.GetMethod("GenericMethod")!.MakeGenericMethod(typeof(string));
            var genericField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(
                generic
            );
            Check(
                genericField.Name == "Pointer"
                    && genericField.DeclaringType == typeof(MethodInfoStoreGeneric_Control<string>)
                    && (nint)genericField.GetValue(null)! == 0xabcd,
                "generic method exact pointer store"
            );
        }
    }

    private static void LocalizationConfiguration(string root)
    {
        var path = Path.Combine(
            root,
            "work/checks/localization-config",
            Guid.NewGuid().ToString("N"),
            "AtelierResleriana.Plugin.Localization.cfg"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            File.ReadAllText(
                Path.Combine(root, ".config/AtelierResleriana.Plugin.Localization.cfg")
            ) + "\n[General]\nLanguage = zh-TW\nDialogueFontSize = 36\n"
        );
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
        Check(
            !config.Bind("Data", "AutoUpdate", true).Value,
            "Bep parses disabled data auto-update"
        );
        Check(
            config.Bind("General", "Language", "en").Value == "zh-TW",
            "Bep preserves user locale"
        );
        config.Save();
        var reopened = new ConfigFile(path, false) { SaveOnConfigSet = false };
        Check(
            !reopened.Bind("Data", "AutoUpdate", true).Value
                && reopened.Bind("General", "Language", "en").Value == "zh-TW"
                && reopened.Bind("General", "DialogueFontSize", 32).Value == 36,
            "Bep save preserves pinned updater and other user options"
        );
    }

    private static void NativeLogCallback()
    {
        var messages = new List<(string Message, LogLevel Level)>();
        var first = CreateLogSource(messages);
        ForceCollection();
        Check(first.Source.TryGetTarget(out var source), "native logger source must survive GC");
        InvokeLogCallback(first.Pointer, "  after collection  ");
        Check(
            messages.Count == 1 && messages[0] == ("after collection", LogLevel.Message),
            "rooted native logger callback"
        );
        var second = CreateLogSource(messages);
        source!.Dispose();
        InvokeLogCallback(second.Pointer, "replacement");
        Check(messages.Count == 2 && messages[1].Message == "replacement", "old source disposal");
        Throws<InvalidOperationException>(() =>
            new IL2CPPLogSource(_ => throw new InvalidOperationException("registration failure"))
        );
        InvokeLogCallback(second.Pointer, "after failed registration");
        Check(messages.Count == 3, "failed registration retains the preceding source");
        Check(second.Source.TryGetTarget(out var replacement), "replacement source root");
        replacement!.Dispose();
        ForceCollection();
        InvokeLogCallback(second.Pointer, "after disposal");
        Check(messages.Count == 3, "disposed source callback remains valid without reporting");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<IL2CPPLogSource> Source, nint Pointer) CreateLogSource(
        List<(string Message, LogLevel Level)> messages
    )
    {
        nint pointer = 0;
        var source = new IL2CPPLogSource(value => pointer = value);
        source.LogEvent += (_, args) => messages.Add((args.Data.ToString()!, args.Level));
        return (new WeakReference<IL2CPPLogSource>(source), pointer);
    }

    private static void ForceCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
    }

    private static void PriorityLogger()
    {
        var preceding = BepInEx.Logging.Logger.Listeners.ToArray();
        var order = new List<string>();
        var failures = new List<(ILogListener Listener, Exception Error, LogEventArgs Original)>();
        var error = new IOException("synthetic sink failure");
        var source = new ManualLogSource("priority-control");
        var first = new ListenerProbe(() => throw error, () => order.Add("normal"));
        var later = new ListenerProbe(() => LogLevel.All, () => order.Add("later"));
        var priority = new PriorityProbe(order, failures);
        var otherPriority = new PriorityProbe(order, failures);
        BepInEx.Logging.Logger.Listeners.Clear();
        var sourceCount = BepInEx.Logging.Logger.Sources.Count;
        BepInEx.Logging.Logger.Sources.Add(source);
        try
        {
            Check(
                BepInEx.Logging.Logger.Sources.Count == sourceCount + 1,
                "source registered exactly once"
            );
            first.ThrowFilter = false;
            BepInEx.Logging.Logger.Listeners.Add(first);
            BepInEx.Logging.Logger.Listeners.Add(later);
            BepInEx.Logging.Logger.Listeners.Add(priority);
            BepInEx.Logging.Logger.Listeners.Add(otherPriority);
            first.ThrowFilter = true;
            source.Log(LogLevel.Error, "original");
            Check(
                order.SequenceEqual(
                    new[] { "priority", "priority", "failure", "failure", "later" }
                ),
                "priority before failing filter"
            );
            Check(
                failures.Count == 2
                    && ReferenceEquals(failures[0].Listener, first)
                    && ReferenceEquals(failures[0].Error, error)
                    && failures[0].Original.Data.ToString() == "original",
                "original listener failure context"
            );
            order.Clear();
            first.ThrowFilter = false;
            first.ThrowOutput = true;
            source.Log(LogLevel.Warning, "second");
            Check(
                order.SequenceEqual(
                    new[] { "priority", "priority", "failure", "failure", "later" }
                ),
                "priority before failing output"
            );
            BepInEx.Logging.Logger.Listeners.Remove(priority);
            BepInEx.Logging.Logger.Listeners.Remove(otherPriority);
            order.Clear();
            Throws<IOException>(() => source.Log(LogLevel.Error, "upstream behavior"));
            Check(order.Count == 0, "without priority normal failure still propagates");
        }
        finally
        {
            BepInEx.Logging.Logger.Listeners.Clear();
            foreach (var listener in preceding)
                BepInEx.Logging.Logger.Listeners.Add(listener);
            Check(
                BepInEx.Logging.Logger.Sources.Remove(source)
                    && BepInEx.Logging.Logger.Sources.Count == sourceCount,
                "source removal restores collection"
            );
            source.Dispose();
        }
    }

    private static void ConsoleSuppression()
    {
        if (BepInEx.Paths.BepInExConfigPath is null)
        {
            var configuration = Path.Combine(FindRoot(), "work/checks/warning-console-config");
            Directory.CreateDirectory(configuration);
            typeof(BepInEx.Paths)
                .GetProperty(nameof(BepInEx.Paths.BepInExConfigPath))!
                .SetValue(null, Path.Combine(configuration, "BepInEx.cfg"));
        }
        var source = new ManualLogSource("console-warning-control");
        var args = new LogEventArgs("original warning", LogLevel.Warning, source);
        Check(!args.SuppressConsole, "console suppression defaults off");
        var core = typeof(LogEventArgs).Assembly;
        var manager = core.GetType("BepInEx.ConsoleManager", true)!;
        var driverProperty = manager.GetProperty(
            "Driver",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var precedingDriver = driverProperty.GetValue(null);
        var driverType = core.GetType("BepInEx.Unix.LinuxConsoleDriver", true)!;
        var driver = RuntimeHelpers.GetUninitializedObject(driverType);
        using var output = new StringWriter();
        driverType.GetProperty("StdoutRedirected")!.SetValue(driver, true);
        driverType.GetProperty("ConsoleOut")!.SetValue(driver, output);
        driverProperty.SetValue(null, driver);
        var preceding = BepInEx.Logging.Logger.Listeners.ToArray();
        var order = new List<string>();
        var failures = new List<(ILogListener Listener, Exception Error, LogEventArgs Original)>();
        var captureFails = false;
        var capture = new PriorityProbe(
            order,
            failures,
            eventArgs =>
            {
                if (captureFails)
                    return;
                eventArgs.SuppressConsole = true;
            }
        );
        var console = new ConsoleLogListener();
        var received = new List<LogEventArgs>();
        var original = typeof(BepInEx.Logging.Logger).GetMethod(
            "InternalLogEvent",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        try
        {
            console.LogEvent(source, args);
            Check(output.ToString() == args.ToStringLine(), "first warning remains visible");
            output.GetStringBuilder().Clear();
            args.SuppressConsole = true;
            console.LogEvent(source, args);
            Check(output.ToString().Length == 0, "flagged warning skips actual console output");
            Check(
                args.Data.Equals("original warning")
                    && args.Level == LogLevel.Warning
                    && ReferenceEquals(args.Source, source),
                "console flag preserves original event"
            );
            foreach (
                var level in new[]
                {
                    LogLevel.Error,
                    LogLevel.Fatal,
                    LogLevel.Error | LogLevel.Warning,
                    LogLevel.Info,
                }
            )
            {
                var important = new LogEventArgs("original diagnostic", level, source)
                {
                    SuppressConsole = true,
                };
                console.LogEvent(source, important);
                Check(
                    output.ToString() == important.ToStringLine(),
                    "only exact warning may suppress console"
                );
                output.GetStringBuilder().Clear();
            }
            BepInEx.Logging.Logger.Listeners.Clear();
            BepInEx.Logging.Logger.Listeners.Add(console);
            BepInEx.Logging.Logger.Listeners.Add(new EventProbe(received));
            BepInEx.Logging.Logger.Listeners.Add(capture);
            args = new LogEventArgs("durably repeated warning", LogLevel.Warning, source);
            original.Invoke(null, [source, args]);
            Check(
                output.ToString().Length == 0
                    && args.SuppressConsole
                    && ReferenceEquals(received.Single(), args),
                "priority suppression precedes console and preserves other listener event"
            );
            captureFails = true;
            args = new LogEventArgs("unacknowledged warning", LogLevel.Warning, source);
            original.Invoke(null, [source, args]);
            Check(
                !args.SuppressConsole
                    && output.ToString() == args.ToStringLine()
                    && received.Count == 2,
                "failed capture does not suppress original console warning"
            );
            output.GetStringBuilder().Clear();
            using var entered = new ManualResetEventSlim();
            using var acknowledged = new ManualResetEventSlim();
            BepInEx.Logging.Logger.Listeners.Remove(capture);
            capture = new PriorityProbe(
                order,
                failures,
                eventArgs =>
                {
                    entered.Set();
                    acknowledged.Wait();
                    eventArgs.SuppressConsole = true;
                }
            );
            BepInEx.Logging.Logger.Listeners.Add(capture);
            args = new LogEventArgs("held durable warning", LogLevel.Warning, source);
            var publication = Task.Run(() => original.Invoke(null, [source, args]));
            try
            {
                Check(
                    entered.Wait(TimeSpan.FromSeconds(5)),
                    "priority warning entered before acknowledgement"
                );
                Check(
                    !publication.IsCompleted
                        && !args.SuppressConsole
                        && output.ToString().Length == 0
                        && received.Count == 2,
                    "console and normal sinks wait for priority acknowledgement"
                );
            }
            finally
            {
                acknowledged.Set();
            }
            publication.GetAwaiter().GetResult();
            Check(
                args.SuppressConsole && output.ToString().Length == 0 && received.Count == 3,
                "acknowledged repeat suppresses console only"
            );
        }
        finally
        {
            BepInEx.Logging.Logger.Listeners.Clear();
            foreach (var listener in preceding)
                BepInEx.Logging.Logger.Listeners.Add(listener);
            driverProperty.SetValue(null, precedingDriver);
            source.Dispose();
        }
    }

    private static unsafe void InvokeLogCallback(nint pointer, string message)
    {
        var buffer = Marshal.StringToCoTaskMemUTF8(message);
        try
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)pointer)(buffer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    private static void Lifecycle()
    {
        Unary target = value => value + 1;
        var hook = new ProbeDetour(target);
        var original = hook.GenerateTrampoline<Unary>();
        Check(original(4) == 5 && hook.Prepares == 1, "direct delegate trampoline");
        hook.Apply();
        hook.Apply();
        Check(hook.Applies == 1 && hook.IsApplied, "apply idempotence");
        hook.Undo();
        hook.Undo();
        Check(hook.Undoes == 1 && !hook.IsApplied, "undo state");
        hook.Apply();
        hook.Dispose();
        hook.Dispose();
        Check(hook.Frees == 1 && !hook.IsValid && !hook.IsApplied, "dispose idempotence");
        Throws<ObjectDisposedException>(() => hook.Apply());
        var failure = new ProbeDetour(target) { FailApply = true };
        Throws<InvalidOperationException>(() => failure.Apply());
        Check(failure.Frees == 1 && !failure.IsValid, "failed apply cleanup");
        var prepareFailure = new ProbeDetour(target) { FailPrepare = true };
        Throws<InvalidOperationException>(() => prepareFailure.Apply());
        Check(prepareFailure.Frees == 1 && !prepareFailure.IsValid, "failed prepare cleanup");
        GC.KeepAlive(target);
    }

    private static void ManagedProxyLifecycle()
    {
        if (
            !OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64
        )
            throw new PlatformNotSupportedException("Native proxy control requires Windows x64");
        var native = VirtualAlloc(0, 16, 0x3000, 0x04);
        Check(native != 0, "native control allocation");
        try
        {
            // Owned x64 stub: mov eax, 6; ret. It cannot invoke managed code or game exports.
            Marshal.Copy(new byte[] { 0xb8, 6, 0, 0, 0, 0xc3 }, 0, native, 6);
            Check(VirtualProtect(native, 16, 0x20, out _), "native control executable protection");
            Unary target = value => value + 2;
            using var hook = new ProbeDetour(native, target);
            var signature = typeof(Unary).GetMethod("Invoke")!;
            var proxy = hook.GenerateTrampoline(signature);
            Check((int)proxy.Invoke(null, [4])! == 6, "managed native proxy invocation");
            hook.Apply();
            hook.Undo();
            var type = hook.GetType().BaseType!;
            Check(
                type.GetField("_trampolinePin", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(hook)
                    is null,
                "managed proxy pin released"
            );
            var nativeDetour = type.GetField(
                        "_trampolineDetour",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    )!
                .GetValue(hook)!;
            Check(
                nativeDetour.Equals(Activator.CreateInstance(nativeDetour.GetType())),
                "managed proxy detour handles released"
            );
            var replacement = hook.GenerateTrampoline(signature);
            Check(!ReferenceEquals(proxy, replacement), "managed proxy reconstructed after undo");
            Check((int)replacement.Invoke(null, [5])! == 6, "replacement proxy invocation");
            hook.Dispose();
            Check(
                type.GetField("_trampolinePin", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(hook)
                    is null,
                "disposed proxy pin released"
            );
            GC.KeepAlive(target);
        }
        finally
        {
            Check(VirtualFree(native, 0, 0x8000), "native control release");
        }
    }

    private static void GuardedScanner(MethodInfo scan)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var page = Environment.SystemPageSize;
        var allocation = VirtualAlloc(0, (nuint)(page * 2), 0x3000, 0x04);
        if (allocation == 0)
            throw new InvalidOperationException("VirtualAlloc failed");
        try
        {
            Check(
                VirtualProtect(allocation + page, (nuint)page, 0x01, out _),
                "guard page protection"
            );
            Marshal.WriteByte(allocation + page - 1, 0x75);
            Check(
                (nint)
                    scan.Invoke(
                        null,
                        [
                            allocation,
                            (long)page,
                            new[] { (char)0x75, (char)0x71 },
                            new[] { 'x', 'x' },
                            0L,
                        ]
                    )! == 0,
                "scanner must not read into no-access page"
            );
        }
        finally
        {
            Check(VirtualFree(allocation, 0, 0x8000), "VirtualFree");
        }
    }

    private static void ReadableRangeScanner(Type scanType)
    {
        var scan = scanType.GetMethod(
            "FindSignatureInMemoryRange",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        var page = Environment.SystemPageSize;
        var allocation = VirtualAlloc(0, (nuint)(page * 3), 0x3000, 0x04);
        Check(allocation != 0, "range control allocation");
        try
        {
            Marshal.WriteByte(allocation + page - 1, 0x75);
            Marshal.Copy(new byte[] { 0x75, 0x71 }, 0, allocation + page * 2 + 12, 2);
            Check(VirtualProtect(allocation + page, (nuint)page, 0x01, out _), "unreadable range");
            var pattern = new[] { (char)0x75, (char)0x71 };
            var mask = new[] { 'x', 'x' };
            Check(
                (nint)scan.Invoke(null, [allocation, (long)page * 3, pattern, mask, 0L])!
                    == allocation + page * 2 + 12,
                "range scanner skips no-access page"
            );
            Check(
                (nint)scan.Invoke(null, [allocation + page * 2, 13L, pattern, mask, 0L])! == 0,
                "VirtualQuery region must be clipped to requested range"
            );
            Check(VirtualProtect(allocation + page * 2, (nuint)page, 0x104, out _), "guard range");
            Check(
                (nint)scan.Invoke(null, [allocation, (long)page * 3, pattern, mask, 0L])! == 0,
                "range scanner skips guard page"
            );
            Check(
                VirtualProtect(allocation + page * 2, (nuint)page, 0x04, out var preceding)
                    && preceding == 0x104,
                "range scanner preserves guard protection"
            );
            Check(
                VirtualProtect(allocation + page, (nuint)page, 0x04, out preceding)
                    && preceding == 0x01,
                "range scanner preserves no-access protection"
            );
        }
        finally
        {
            Check(VirtualFree(allocation, 0, 0x8000), "range control release");
        }
    }

    private static void SourceAndRuntimeContracts(string root)
    {
        using var sdk = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        Check(
            sdk.RootElement.GetProperty("sdk").GetProperty("version").GetString() == "10.0.402",
            "SDK pin"
        );
        var project = File.ReadAllText(
            Path.Combine(root, "Sources/AtelierCapture/AtelierCapture.csproj")
        );
        Check(
            project.Contains(
                "<TargetFramework>net10.0</TargetFramework>",
                StringComparison.Ordinal
            ),
            "product target"
        );
        Check(
            !project.Contains("GameDirectory", StringComparison.Ordinal),
            "public reference contract"
        );
        Check(
            File.ReadAllText(Path.Combine(root, "VERSION")).Trim() == "1.4.0"
                && project.Contains("<Version>1.4.0</Version>", StringComparison.Ordinal),
            "schema 5 candidate identity"
        );
        var stage = Path.Combine(root, "work/build/stage");
        var missingDependencies = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(stage, "BepInEx/core"), "*.dll"))
        {
            using var assemblyStream = File.OpenRead(path);
            using var image = new PEReader(assemblyStream);
            if (!image.HasMetadata)
                continue;
            var metadata = image.GetMetadataReader();
            foreach (var reference in metadata.AssemblyReferences)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference(reference).Name);
                var generated =
                    name == "Il2Cppmscorlib"
                    || name == "Unity.InputSystem"
                    || name.StartsWith("UnityEngine.", StringComparison.Ordinal);
                if (
                    !generated
                    && !File.Exists(Path.Combine(stage, "BepInEx/core", name + ".dll"))
                    && !File.Exists(Path.Combine(stage, "dotnet", name + ".dll"))
                )
                    missingDependencies.Add(Path.GetFileName(path) + " -> " + name);
            }
        }
        Check(
            missingDependencies.Count == 0,
            "missing offline runtime dependencies: " + string.Join(", ", missingDependencies)
        );
        foreach (
            var pin in new[]
            {
                ("0Harmony.dll", "2.16.1"),
                ("MonoMod.RuntimeDetour.dll", "25.3.6"),
                ("MonoMod.Utils.dll", "25.0.14"),
                ("MonoMod.Core.dll", "1.3.6"),
                ("TerraFX.Interop.Windows.dll", "10.0.22621.2"),
            }
        )
        {
            var assembly = Assembly.LoadFrom(Path.Combine(stage, "BepInEx/core", pin.Item1));
            var information = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            Check(
                information?.StartsWith(pin.Item2, StringComparison.Ordinal) == true,
                "coherent runtime package: " + pin.Item1
            );
        }
        var version = FileVersionInfo.GetVersionInfo(Path.Combine(stage, "dotnet/coreclr.dll"));
        Check(version.FileMajorPart == 10 && version.FileMinorPart == 0, "embedded runtime major");
        using var runtimeArchive = ZipFile.OpenRead(
            Path.Combine(root, "work/build/dependencies/dotnet-runtime-10.0.13.zip")
        );
        foreach (
            var entry in runtimeArchive.Entries.Where(entry =>
                entry.FullName.StartsWith(
                    "shared/Microsoft.NETCore.App/10.0.13/",
                    StringComparison.Ordinal
                )
                && entry.Name.Length != 0
            )
        )
        {
            using var expected = entry.Open();
            using var actual = File.OpenRead(Path.Combine(stage, "dotnet", entry.Name));
            Check(
                SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(actual)),
                "embedded runtime archive bytes: " + entry.Name
            );
        }
        var baseline = Path.Combine(root, "work/build/dependencies/2022.3.62.zip");
        using var stream = File.OpenRead(baseline);
        Check(
            Convert.ToHexStringLower(SHA256.HashData(stream))
                == "575e7d600f69de8200ccf4db700b3ae6252366c22e8c3434c860e428974518d1",
            "public Unity libraries pin"
        );
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/release.yml"));
        Check(
            workflow.Contains("workflow_dispatch:", StringComparison.Ordinal)
                && !workflow.Contains("push:", StringComparison.Ordinal)
                && !workflow.Contains("pull_request:", StringComparison.Ordinal)
                && !workflow.Contains("ubuntu", StringComparison.Ordinal),
            "manual Windows verification"
        );
    }

    private static string FindRoot()
    {
        for (
            var directory = new DirectoryInfo(Environment.CurrentDirectory);
            directory is not null;
            directory = directory.Parent
        )
            if (File.Exists(Path.Combine(directory.FullName, "scripts/build.py")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Run build checks within the source workspace");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (TargetInvocationException error) when (error.InnerException is T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Unary(int value);

    private sealed class ProbeDetour : BaseNativeDetour<ProbeDetour>
    {
        internal ProbeDetour(Unary target)
            : this(Marshal.GetFunctionPointerForDelegate(target), target) { }

        internal ProbeDetour(nint original, Unary target)
            : base(original, target) { }

        internal int Prepares,
            Applies,
            Undoes,
            Frees;
        internal bool FailApply,
            FailPrepare;

        protected override void PrepareImpl()
        {
            Prepares++;
            TrampolinePtr = OriginalMethodPtr;
            if (FailPrepare)
                throw new InvalidOperationException("injected prepare failure");
        }

        protected override void ApplyImpl()
        {
            Applies++;
            if (FailApply)
                throw new InvalidOperationException("injected apply failure");
        }

        protected override void UndoImpl() => Undoes++;

        protected override void FreeImpl() => Frees++;
    }

    private sealed class ListenerProbe(Func<LogLevel> filter, Action output) : ILogListener
    {
        internal bool ThrowFilter,
            ThrowOutput;
        public LogLevel LogLevelFilter => ThrowFilter ? filter() : LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (ThrowOutput)
                throw new IOException("synthetic output failure");
            output();
        }

        public void Dispose() { }
    }

    private sealed class PriorityProbe(
        List<string> order,
        List<(ILogListener Listener, Exception Error, LogEventArgs Original)> failures,
        Action<LogEventArgs>? capture = null
    ) : IPriorityLogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            order.Add("priority");
            capture?.Invoke(eventArgs);
        }

        public void LogListenerFailure(
            ILogListener failedListener,
            Exception error,
            LogEventArgs original
        )
        {
            order.Add("failure");
            failures.Add((failedListener, error, original));
        }

        public void Dispose() { }
    }

    private sealed class EventProbe(List<LogEventArgs> received) : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs) => received.Add(eventArgs);

        public void Dispose() { }
    }

    private sealed class PointerFixture
    {
        public static readonly nint NativeMethodInfoPtr_Constructor = 0x1234;
        public static readonly nint NativeMethodInfoPtr_Method = 0x5678;
        public static readonly nint NativeFieldInfoPtr_Value = 0x9876;
        public readonly nint Value;

        public PointerFixture() => Value = NativeMethodInfoPtr_Constructor;

        public nint Method() => NativeMethodInfoPtr_Method;

        public nint FieldAccessor() => NativeFieldInfoPtr_Value;

        public nint GenericMethod<T>() => MethodInfoStoreGeneric_Control<T>.Pointer;
    }

    private sealed class PointerFixture<T>
    {
        public static readonly nint NativeMethodInfoPtr_Constructor = 0x1234;
        public static readonly nint NativeMethodInfoPtr_Method = 0x5678;
        public static readonly nint NativeFieldInfoPtr_Value = 0x9876;
        public readonly nint Value;

        public PointerFixture() => Value = NativeMethodInfoPtr_Constructor;

        public nint Method() => NativeMethodInfoPtr_Method;

        public nint FieldAccessor() => NativeFieldInfoPtr_Value;

        public nint GenericMethod<U>() => MethodInfoStoreGeneric_Control<U>.Pointer;
    }

    private static class MethodInfoStoreGeneric_Control<T>
    {
        public static readonly nint Pointer = 0xabcd;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(
        nint address,
        nuint size,
        uint allocationType,
        uint protection
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(
        nint address,
        nuint size,
        uint newProtection,
        out uint oldProtection
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
