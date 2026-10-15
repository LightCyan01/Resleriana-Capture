namespace AtelierCapture;

internal static class Atelier
{
    internal static Battle.HookSpec[] Contracts()
    {
        var contract = Plugin.ReadResource<ContractSet>("Gameplay.AtelierHooks.json");
        foreach (var gap in contract.Unqualified)
            Battle.RecordGap("Atelier:" + gap, "native_entry_contract_unqualified");
        return contract.Hooks;
    }

    private sealed record ContractSet(Battle.HookSpec[] Hooks, string[] Unqualified);
}
