namespace AtelierCapture;

internal sealed record RequestEntityFraming(
    string? method,
    bool upload_stream_present,
    bool original_entity_returned_null,
    HeaderSnapshot? serialized_headers
);

internal static partial class Network
{
    internal static bool IsVerifiedBodilessRequest(RequestEntityFraming framing)
    {
        var headers = framing.serialized_headers;
        if (
            framing.method != "Get"
            || framing.upload_stream_present
            || !framing.original_entity_returned_null
            || headers is null
            || !headers.complete
            || headers.error is not null
            || headers.groups.Any(group =>
                !group.complete || group.name is null || group.error is not null
            )
        )
            return false;
        var lengths = new List<string?>();
        foreach (var group in headers.groups)
        {
            if (group.name!.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!group.name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (group.values is null)
                return false;
            lengths.AddRange(group.values);
        }
        return lengths.Count == 1 && lengths[0] == "0";
    }

    internal static void RecordNullRequestEntity(
        RequestLedger.Attempt attempt,
        List<Action> commits
    )
    {
        Emit(
            attempt.Context,
            "request_entity_observation",
            new
            {
                request_id = attempt.Context.RequestId,
                attempt_id = attempt.Context.AttemptId,
                phase = "original_GetEntityBody_return",
                original_native_value = (byte[]?)null,
                entity_status = "observed_null_pending_serialized_framing",
                source = "BestHTTP.HTTPRequest.GetEntityBody@0x9A55D0_original_return",
            }
        );
        commits.Add(() => attempt.RequestEntityReturnedNull = true);
    }

    internal static void RecordRequestEntityCompletion(
        RequestLedger.Attempt attempt,
        RequestEntityFraming? proof
    )
    {
        if (proof is not null && IsVerifiedBodilessRequest(proof))
            Emit(
                attempt.Context,
                "request_entity_observation",
                new
                {
                    request_id = attempt.Context.RequestId,
                    attempt_id = attempt.Context.AttemptId,
                    phase = "before_dispose",
                    original_native_value = (byte[]?)null,
                    entity_status = "verified_bodiless_by_original_serialized_headers",
                    framing = proof,
                }
            );
        else
            Gap(
                attempt.Context,
                "request_entity_never_materialized",
                new { request_id = attempt.Context.RequestId, framing = proof }
            );
    }
}
