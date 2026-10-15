namespace AtelierCapture;

internal sealed record ResponseEntityFraming(
    string? method,
    int status,
    bool request_streaming,
    bool response_streaming,
    bool transport_error,
    HeaderSnapshot headers
);

internal static partial class Network
{
    internal static bool IsVerifiedEmptyEntity(ResponseEntityFraming framing)
    {
        if (
            framing.method != "Post"
            || framing.status != 200
            || framing.request_streaming
            || framing.response_streaming
            || framing.transport_error
            || !framing.headers.complete
            || framing.headers.error is not null
            || framing.headers.groups.Any(group =>
                !group.complete || group.name is null || group.error is not null
            )
        )
            return false;
        var lengths = new List<string?>();
        foreach (var header in framing.headers.groups)
        {
            if (
                header.name!.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)
                || header.name.Equals("content-encoding", StringComparison.OrdinalIgnoreCase)
                || header.name.Equals("x-content-encoding", StringComparison.OrdinalIgnoreCase)
            )
                return false;
            if (!header.name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (header.values is null)
                return false;
            lengths.AddRange(header.values);
        }
        return lengths.Count == 1 && lengths[0] == "0";
    }

    internal static object EmptyEntityBody(ResponseEntityFraming proof)
    {
        if (!IsVerifiedEmptyEntity(proof))
            throw new InvalidDataException(
                "Null response entity has no complete empty-body proof."
            );
        return new
        {
            stage = "response_entity",
            original_native_value = (byte[]?)null,
            entity_status = "verified_empty_by_native_framing",
            capture_status = "observed",
            framing = proof,
            native_contract = "PMLKMPHGENK.AMIKMFABIFK@0x4BBB3D0:null_Data_to_empty_result",
            native_decoder_result_recorded_only_at_original_return = true,
        };
    }
}
