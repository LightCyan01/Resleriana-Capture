using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using MonoMod.Cil;
using MonoMod.Core.Platforms;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace BepInEx.Unity.IL2CPP.Hook;

internal abstract class BaseNativeDetour<T> : INativeDetour where T : BaseNativeDetour<T>
{
    private static readonly ConstructorInfo InvalidOperationExceptionCtor =
        typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) });

    protected static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(typeof(T).Name);

    protected BaseNativeDetour(nint originalMethodPtr, Delegate detourMethod)
    {
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

    public nint OriginalMethodPtr { get; }
    public nint DetourMethodPtr { get; }
    public nint TrampolinePtr { get; protected set; }
    public bool IsValid { get; private set; } = true;
    public bool IsApplied { get; private set; }

    public void Dispose()
    {
        if (!IsValid) return;
        Undo();
        Free();
    }

    public void Apply()
    {
        if (IsApplied) return;

        Prepare();
        ApplyImpl();

        Logger.Log(LogLevel.Debug,
                   $"Original: {OriginalMethodPtr:X}, Trampoline: {TrampolinePtr:X}, diff: {Math.Abs(OriginalMethodPtr - TrampolinePtr):X}");

        IsApplied = true;
    }

    public void Undo()
    {
        if (IsApplied && IsPrepared) UndoImpl();
    }

    public void Free()
    {
        FreeImpl();
        IsValid = false;
    }

    public MethodBase GenerateTrampoline(MethodBase signature = null)
    {
        if (TrampolineMethod == null)
        {
            Prepare();
            TrampolineMethod = GenerateNativeProxy(TrampolinePtr, signature);
        }

        return TrampolineMethod;
    }

    public TDelegate GenerateTrampoline<TDelegate>() where TDelegate : Delegate
    {
        if (!typeof(Delegate).IsAssignableFrom(typeof(TDelegate)))
            throw new InvalidOperationException($"Type {typeof(TDelegate)} not a delegate type.");

        _ = GenerateTrampoline(typeof(TDelegate).GetMethod("Invoke"));

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(TrampolinePtr);
    }

    protected abstract void ApplyImpl();

    private void Prepare()
    {
        if (IsPrepared) return;
        Logger.LogDebug($"Preparing detour from 0x{OriginalMethodPtr:X2} to 0x{DetourMethodPtr:X2}");
        PrepareImpl();
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

        using var dmd = new DynamicMethodDefinition(
            $"NativeProxy<{(long) target:X16}>", returnType, argTypes);

        // Fill the stub with a throw so that calling it before the detour is applied
        // fails loudly instead of executing garbage.
        var il = new ILContext(dmd.Definition);
        var cursor = new ILCursor(il);

        cursor.EmitLdstr($"{dmd.Definition.Name} should've been detoured!");
        cursor.EmitNewobj(InvalidOperationExceptionCtor);
        cursor.EmitThrow();

        var dm = dmd.Generate();

        var triple = PlatformTriple.Current;
        _trampolinePin = triple.PinMethodIfNeeded(dm);
        triple.Compile(dm);

        var nativeBody = triple.GetNativeMethodBody(dm);
        _trampolineDetour = triple.CreateNativeDetour(nativeBody, target);

        return dm;
    }
}
