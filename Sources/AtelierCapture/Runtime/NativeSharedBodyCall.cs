namespace AtelierCapture;

internal sealed record NativeSharedBodyCall(
    string invocation_id,
    string physical_boundary,
    string[] possible_aliases,
    string? identified_alias,
    string reason,
    string phase
);
