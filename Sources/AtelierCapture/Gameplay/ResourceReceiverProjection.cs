namespace AtelierCapture;

internal static class ResourceReceiverProjection
{
    internal const string Contract = "native_resource_receiver_identity_v1";
    internal const string StateContract = "receiver_state_not_observed";

    internal static bool Applies(string boundary, long rva) =>
        (boundary, rva)
            is
                ("UserDataManager.SetResources", 0x2A85190)
                or
                ("UserDataManager.UpdateResources", 0x2A82230)
                or
                ("UserDataManager.DeleteResourceEntities", 0x2A85070);

    internal static bool IsOwner(string? name, string? module) =>
        name == "UserDataManager" && module == "Blend.dll";

    internal static void ValidateOwner(
        string? name,
        string? module,
        nint pointer,
        nint registeredClass,
        nint actualClass
    )
    {
        if (
            !IsOwner(name, module)
            || pointer == 0
            || registeredClass == 0
            || actualClass != registeredClass
        )
            throw new InvalidDataException("Resource receiver is not the qualified native owner.");
    }

    internal static NativeReference Reference(long lifetime)
    {
        if (lifetime <= 0)
            throw new InvalidDataException("Resource receiver native lifetime is unavailable.");
        return NativeProjection.Reference("UserDataManager", lifetime) with
        {
            state_contract = StateContract,
        };
    }
}
