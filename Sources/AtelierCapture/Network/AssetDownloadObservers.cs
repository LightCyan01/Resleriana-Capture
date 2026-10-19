using System.Reflection;

namespace AtelierCapture;

internal static partial class Network
{
    private static readonly AssetDownloads AssetTraffic = new();

    internal static Func<NativeObservationSnapshot, bool>? AssetFilter(MethodBase target)
    {
        if (
            target.DeclaringType?.FullName == "BestHTTP.HTTPRequest"
            && target.Name is "Send" or "Dispose" or "set_State" or "GetEntityBody" or "SendHeaders"
        )
            return snapshot => IsAssetDownload(snapshot.Instance, out _);
        return target == bindings?.WriteHeaderGroup ? _ => headerSend?.Excluded == true : null;
    }

    private static bool IsAssetDownload(object? request, out string version)
    {
        version = "";
        if (request is null || bindings is null)
            return false;
        try
        {
            lock (Sync)
                if (
                    Objects.ExistingToken(request) is { } token
                    && (RequestDescriptors.ContainsKey(token) || Ledger.Attempts(token).Count != 0)
                )
                    return false;
            return AssetDownloads.TryClassify(
                ParseOriginalUri(bindings.ReadOriginalUri(request)).Parsed,
                bindings.ReadRequestMethod(request),
                false,
                out version
            );
        }
        catch
        {
            // An unproven request stays on the ordinary capture path.
            return false;
        }
    }

    private static void BeginRequestCallback(object __instance, out IDisposable? __state)
    {
        var lifetime = Plugin.BeginNativeCallback();
        __state =
            lifetime is not null && IsAssetDownload(__instance, out _)
                ? new ExcludedRequestScope(lifetime)
                : lifetime;
    }

    internal static void SaveAssetDownloadSummary()
    {
        var summary = AssetTraffic.Snapshot();
        if (summary.versions.Length != 0)
            Plugin.Writer.EmitBootstrap("asset_download_summary", summary);
    }

    private sealed class ExcludedRequestScope(IDisposable lifetime) : IDisposable
    {
        private readonly IDisposable context = PushContext(null);

        public void Dispose()
        {
            context.Dispose();
            lifetime.Dispose();
        }
    }
}
