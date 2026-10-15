namespace AtelierCapture;

internal static class MaterialNre
{
    private const string Prefix =
        "NullReferenceException: Object reference not set to an instance of an object.\r\n"
        + "CBJFMEGGCLP.FOHJOLJKGNP () (at <00000000000000000000000000000000>:0)\n"
        + "CBJFMEGGCLP.IOMNAOKAHCH () (at <00000000000000000000000000000000>:0)\n"
        + "CharacterObject.CallAfterFixPosition () (at <00000000000000000000000000000000>:0)\n";

    internal const string StandardMessage =
        Prefix
        + "CharacterLoopManager+NNICKFAANCF.NNCECFDIAAG () (at <00000000000000000000000000000000>:0)\n"
        + "CharacterLoopManager.LateUpdate () (at <00000000000000000000000000000000>:0)\n";

    internal const string LodMessage =
        Prefix
        + "CharaLODControl.UpdateByLODTime () (at <00000000000000000000000000000000>:0)\n"
        + "CharaLODControl.GLOIEADCOHP (System.Boolean BKMOEGJJECI) (at <00000000000000000000000000000000>:0)\n"
        + "CharaLODControl.UpdateInfo (System.Boolean BKMOEGJJECI) (at <00000000000000000000000000000000>:0)\n";

    internal static bool IsMessage(string? message) =>
        string.Equals(message, StandardMessage, StringComparison.Ordinal)
        || string.Equals(message, LodMessage, StringComparison.Ordinal);

    internal static bool IsEligible(CaptureDiagnostic record) =>
        record.source == "Unity"
        && record.severity == "Error"
        && IsMessage(record.message)
        && record.exception_type is null
        && record.stack is null
        && record.caused_by_event_id is null
        && record.failed_listener is null;
}
