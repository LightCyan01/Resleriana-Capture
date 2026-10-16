namespace AtelierCapture;

internal static class ClientStatusProjection
{
    internal const long Rva = 0x4192D30;
    internal const string Boundary = "BattleStateVariables.GetClientBattleStatus";
    internal const string Contract = "native_client_status_read_set_0x4192d30_v1";
    internal static readonly string[] OwnerFields =
    [
        "m_actionInfoList",
        "m_actionSetupInfoList",
        "_MHHGMPCBOKI_k__BackingField",
        "_ENPKCFFLCFM_k__BackingField",
    ];

    internal static bool Applies(string boundary, long rva) => boundary == Boundary && rva == Rva;

    internal static SnapshotSource Source(
        object identity,
        string type,
        string lifetime,
        Func<string, object?> readOwner,
        Func<object, string, object?> readChild,
        Func<object, NativeReference> reference,
        IReadOnlyList<string> excluded
    )
    {
        var actions = new Lazy<object?>(() => readOwner("m_actionInfoList"));
        var setups = new Lazy<object?>(() => readOwner("m_actionSetupInfoList"));
        var history = new Lazy<object?>(() => readOwner("_ENPKCFFLCFM_k__BackingField"));
        object? Identity(Lazy<object?> field) => field.Value is { } value ? reference(value) : null;
        object? Stored(Lazy<object?> field, string child) =>
            field.Value is { } value ? readChild(value, child) : null;
        return new(
            type,
            identity,
            [
                new("m_actionInfoList", () => Identity(actions)),
                new("m_actionInfoList._size", () => Stored(actions, "_size")),
                new("m_actionSetupInfoList", () => Identity(setups)),
                new("m_actionSetupInfoList._size", () => Stored(setups, "_size")),
                new(
                    "_MHHGMPCBOKI_k__BackingField",
                    () => readOwner("_MHHGMPCBOKI_k__BackingField")
                ),
                new("_ENPKCFFLCFM_k__BackingField", () => Identity(history)),
                new(
                    "_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField",
                    () => Stored(history, "_NMGKEONKAHG_k__BackingField")
                ),
            ],
            Excluded: excluded,
            LifetimeId: lifetime
        );
    }
}
