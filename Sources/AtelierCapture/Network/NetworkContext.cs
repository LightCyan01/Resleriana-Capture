using System.Globalization;

namespace AtelierCapture;

internal sealed record NetworkContext(
    string RequestId,
    string AttemptId,
    string? OperationId,
    string Route,
    string? SystemId,
    bool? IsPrivate,
    string? ResponseId,
    string? IdentityProof,
    string? DescriptorId = null,
    string? LogicalRoute = null,
    string? NativeOperationId = null,
    string? RequestHeaderId = null,
    string? TransportPath = null
);

internal sealed class RequestLedger
{
    private long nextAttempt,
        nextResponse;
    private int activeCount;
    private readonly Dictionary<long, List<Attempt>> requests = new();
    private readonly Dictionary<long, Attempt> responses = new();

    internal int ActiveCount => activeCount;

    internal Attempt Begin(
        long requestToken,
        string route,
        bool? isPrivate,
        string? operationId,
        string? identityProof,
        string? systemId
    )
    {
        var id = (++nextAttempt).ToString(CultureInfo.InvariantCulture);
        var attempt = new Attempt(
            requestToken,
            new NetworkContext(
                id,
                id,
                CanonicalOperation(operationId),
                route,
                systemId,
                isPrivate,
                null,
                identityProof
            )
        );
        if (!requests.TryGetValue(requestToken, out var attempts))
            requests.Add(requestToken, attempts = new());
        attempts.Add(attempt);
        activeCount++;
        return attempt;
    }

    internal IReadOnlyList<Attempt> Attempts(long token) =>
        requests.TryGetValue(token, out var attempts) ? attempts : Array.Empty<Attempt>();

    internal Attempt? ForObservation(long token, NetworkContext? scope) =>
        requests.TryGetValue(token, out var attempts)
            ? scope is not null
                ? attempts.SingleOrDefault(attempt =>
                    attempt.Context.AttemptId == scope.AttemptId
                    && attempt.Context.RequestId == scope.RequestId
                )
                : Unique(token)
            : null;

    internal Attempt? BindResponse(long responseToken, long requestToken)
    {
        if (responses.TryGetValue(responseToken, out var bound))
            return bound.RequestToken == requestToken ? bound : null;
        if (!requests.TryGetValue(requestToken, out var attempts) || attempts.Count != 1)
            return null;
        var attempt = attempts[0];
        if (attempt.Completed)
            return null;
        attempt.Context = attempt.Context with
        {
            ResponseId = (++nextResponse).ToString(CultureInfo.InvariantCulture),
        };
        responses.Add(responseToken, attempt);
        return attempt;
    }

    internal Attempt? Response(long token) => responses.GetValueOrDefault(token);

    internal void VerifyDescriptor(
        long token,
        string descriptorId,
        string logicalRoute,
        string? nativeOperationId
    )
    {
        var current = Unique(token);
        if (
            current?.Context.DescriptorId is { } known
            && (
                known != descriptorId
                || current.Context.LogicalRoute != logicalRoute
                || current.Context.NativeOperationId != nativeOperationId
            )
        )
            throw new InvalidDataException("Previously proven native descriptor owner disagrees.");
    }

    internal void Release(long token)
    {
        responses.Remove(token);
        if (!requests.Remove(token, out var removed))
            return;
        foreach (var attempt in removed)
            if (attempt.ActiveCounted)
            {
                attempt.ActiveCounted = false;
                activeCount--;
            }
    }

    internal void Complete(Attempt attempt)
    {
        attempt.Completed = true;
        if (attempt.ActiveCounted)
        {
            attempt.ActiveCounted = false;
            activeCount--;
        }
        attempt.CompletionPending = false;
    }

    internal static string? CanonicalOperation(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : null;

    internal static bool? Classify(Uri? uri) =>
        uri is null ? null
        : uri.IsLoopback ? true
        : uri.Host.Equals("game.resleriana.jp", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".resleriana.jp", StringComparison.OrdinalIgnoreCase)
            ? false
        : null;

    internal Attempt? Unique(long token) =>
        SingleLifetime(token) is { Completed: false } attempt ? attempt : null;

    internal Attempt? SingleLifetime(long token) =>
        requests.TryGetValue(token, out var attempts) && attempts.Count == 1 ? attempts[0] : null;

    internal sealed class Attempt(long requestToken, NetworkContext context)
    {
        internal long RequestToken { get; } = requestToken;
        internal NetworkContext Context { get; set; } = context;
        internal long Started { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        internal bool ResponsePending,
            ResponseWritten,
            CompletionPending,
            Completed;
        internal bool ActiveCounted = true;
        internal bool RequestEntityReturnedNull;
        internal HeaderSnapshot? SerializedRequestHeaders;
        private readonly HashSet<string> pendingStages = new(StringComparer.Ordinal);
        private readonly HashSet<string> writtenStages = new(StringComparer.Ordinal);

        internal bool ReserveStage(string stage) =>
            !writtenStages.Contains(stage) && pendingStages.Add(stage);

        internal void CancelStage(string stage) => pendingStages.Remove(stage);

        internal void CommitStage(string stage)
        {
            pendingStages.Remove(stage);
            writtenStages.Add(stage);
        }

        internal bool HasStage(string stage) =>
            writtenStages.Any(key => key.StartsWith(stage + ":", StringComparison.Ordinal))
            || pendingStages.Any(key => key.StartsWith(stage + ":", StringComparison.Ordinal));

        internal bool StageWritten(string stage) => writtenStages.Contains(stage);
    }
}
