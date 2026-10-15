using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using MonoMod.Cil;
using MonoMod.Core.Platforms;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace BepInEx.Unity.IL2CPP.Hook;

internal abstract class BaseNativeDetour<T> : INativeDetour
    where T : BaseNativeDetour<T>
{
    private static readonly ConstructorInfo InvalidOperationExceptionCtor =
        typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) });

    protected static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(typeof(T).Name);

    protected BaseNativeDetour(nint originalMethodPtr, Delegate detourMethod)
    {
        if (originalMethodPtr == 0)
            throw new ArgumentException("Native target is required", nameof(originalMethodPtr));
        if (detourMethod == null)
            throw new ArgumentNullException(nameof(detourMethod));
        OriginalMethodPtr = originalMethodPtr;
        DetourMethod = detourMethod;
        DetourMethodPtr = Marshal.GetFunctionPointerForDelegate(detourMethod);
    }

    public DetourConfig Config { get; } = new(null, null, null, null);

    public bool IsPrepared { get; protected set; }
    protected MethodInfo TrampolineMethod { get; set; }
    protected Delegate DetourMethod { get; set; }

    private PlatformTriple.NativeDetour _trampolineDetour;
    private IDisposable _trampolinePin;
    private readonly object lifecycle = new();

    public nint OriginalMethodPtr { get; }
    public nint DetourMethodPtr { get; }
    public nint TrampolinePtr { get; protected set; }
    public bool IsValid { get; private set; } = true;
    public bool IsApplied { get; private set; }

    public void Dispose()
    {
        Free();
    }

    public void Apply()
    {
        lock (lifecycle)
        {
            if (!IsValid)
                throw new ObjectDisposedException(GetType().Name);
            if (IsApplied)
                return;
            try
            {
                Prepare();
                ApplyImpl();
                IsApplied = true;
                Logger.LogDebug($"Original: {OriginalMethodPtr:X}, Trampoline: {TrampolinePtr:X}");
            }
            catch
            {
                Free();
                throw;
            }
        }
    }

    public void Undo()
    {
        lock (lifecycle)
        {
            if (!IsApplied)
                return;
            UndoImpl();
            IsApplied = false;
            ReleaseProxy();
        }
    }

    public void Free()
    {
        lock (lifecycle)
        {
            if (!IsValid)
                return;
            Undo();
            ReleaseProxy();
            FreeImpl();
            IsValid = false;
            IsPrepared = false;
            TrampolinePtr = 0;
        }
    }

    public MethodBase GenerateTrampoline(MethodBase signature = null)
    {
        if (signature == null)
            throw new ArgumentNullException(nameof(signature));
        lock (lifecycle)
        {
            if (!IsValid)
                throw new ObjectDisposedException(GetType().Name);
            if (TrampolineMethod == null)
            {
                Prepare();
                TrampolineMethod = GenerateNativeProxy(TrampolinePtr, signature);
            }
            return TrampolineMethod;
        }
    }

    public TDelegate GenerateTrampoline<TDelegate>()
        where TDelegate : Delegate
    {
        if (!typeof(Delegate).IsAssignableFrom(typeof(TDelegate)))
            throw new InvalidOperationException($"Type {typeof(TDelegate)} not a delegate type.");

        lock (lifecycle)
        {
            if (!IsValid)
                throw new ObjectDisposedException(GetType().Name);
            Prepare();
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(TrampolinePtr);
        }
    }

    protected abstract void ApplyImpl();

    private void Prepare()
    {
        if (IsPrepared)
            return;
        Logger.LogDebug($"Preparing detour from 0x{OriginalMethodPtr:X2} to 0x{DetourMethodPtr:X2}");
        try
        {
            PrepareImpl();
        }
        catch
        {
            Free();
            throw;
        }
        Logger.LogDebug($"Prepared detour; Trampoline: 0x{TrampolinePtr:X2}");
        IsPrepared = true;
    }

    protected abstract void PrepareImpl();

    protected abstract void UndoImpl();

    protected abstract void FreeImpl();

    /// <summary>
    ///     Creates a managed method with the same signature as <paramref name="signature" /> that,
    ///     once called, executes the native function at <paramref name="target" />.
    /// </summary>
    /// <remarks>
    ///     Replaces MonoMod 22's <c>DetourHelper.GenerateNativeProxy</c>: a stub method is generated
    ///     and its native entry point is detoured to <paramref name="target" />.
    ///     The returned proxy is kept alive for the lifetime of this detour.
    /// </remarks>
    private MethodInfo GenerateNativeProxy(nint target, MethodBase signature)
    {
        var returnType = (signature as MethodInfo)?.ReturnType ?? typeof(void);
        var args = signature.GetParameters();
        var argTypes = new Type[args.Length];
        for (var i = 0; i < args.Length; i++)
            argTypes[i] = args[i].ParameterType;

        using var dmd = new DynamicMethodDefinition($"NativeProxy<{(long)target:X16}>", returnType, argTypes);

        // Fill the stub with a throw so that calling it before the detour is applied
        // fails loudly instead of executing garbage.
        var il = new ILContext(dmd.Definition);
        var cursor = new ILCursor(il);

        cursor.EmitLdstr($"{dmd.Definition.Name} should've been detoured!");
        cursor.EmitNewobj(InvalidOperationExceptionCtor);
        cursor.EmitThrow();

        var dm = dmd.Generate();

        var triple = PlatformTriple.Current;
        try
        {
            _trampolinePin = triple.PinMethodIfNeeded(dm);
            triple.Compile(dm);
            var nativeBody = triple.GetNativeMethodBody(dm);
            _trampolineDetour = triple.CreateNativeDetour(nativeBody, target);
            return dm;
        }
        catch
        {
            ReleaseProxy();
            throw;
        }
    }

    private void ReleaseProxy()
    {
        // Lifecycle callers must drain native invocations before unpatching a hook.
        _trampolineDetour.Simple?.Dispose();
        _trampolineDetour.AltHandle?.Dispose();
        _trampolineDetour = default;
        _trampolinePin?.Dispose();
        _trampolinePin = null;
        TrampolineMethod = null;
    }
}
