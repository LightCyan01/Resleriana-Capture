using System;

namespace BepInEx.Unity.IL2CPP.Hook.Dobby;

internal class DobbyDetour : BaseNativeDetour<DobbyDetour>
{
    public DobbyDetour(nint originalMethodPtr, Delegate detourMethod)
        : base(originalMethodPtr, detourMethod) { }

    private bool allocated;

    protected override void ApplyImpl() => EnsureSuccess(DobbyLib.Commit(OriginalMethodPtr), "commit");

    protected override unsafe void PrepareImpl()
    {
        nint trampolinePtr = 0;
        EnsureSuccess(DobbyLib.Prepare(OriginalMethodPtr, DetourMethodPtr, &trampolinePtr), "prepare");
        allocated = true;
        if (trampolinePtr == 0)
            throw new InvalidOperationException("Dobby prepared a null trampoline");
        TrampolinePtr = trampolinePtr;
    }

    protected override void UndoImpl()
    {
        EnsureSuccess(DobbyLib.Destroy(OriginalMethodPtr), "destroy");
        allocated = false;
        IsPrepared = false;
    }

    protected override void FreeImpl()
    {
        if (allocated)
            UndoImpl();
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"Dobby {operation} failed: {result}");
    }
}
