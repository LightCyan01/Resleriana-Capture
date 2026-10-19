namespace AtelierCapture;

internal sealed record AssetDownloadVersion(string version)
{
    public string platform => "StandaloneWindows64";
    public string scope => "excluded_official_cdn_assets";
}

internal sealed record AssetDownloadCount(string version, long observed_send_calls);

internal sealed record AssetDownloadSummary(AssetDownloadCount[] versions)
{
    public string scope => "excluded_official_cdn_assets";
    public string basis => "observed_send_calls_not_completed_downloads";
    public bool final_counts_durable_before_abrupt_exit => false;
}

internal sealed class AssetDownloads
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> sends = new(StringComparer.Ordinal);

    internal bool ObserveSend(string version)
    {
        lock (gate)
        {
            var first = !sends.TryGetValue(version, out var count);
            sends[version] = checked(count + 1);
            return first;
        }
    }

    internal AssetDownloadSummary Snapshot()
    {
        lock (gate)
            return new(
                sends
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new AssetDownloadCount(pair.Key, pair.Value))
                    .ToArray()
            );
    }

    internal static bool TryClassify(Uri? uri, string? method, bool apiOwned, out string version)
    {
        version = "";
        if (
            apiOwned
            || method != "Get"
            || uri is not { IsAbsoluteUri: true }
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443
            || !uri.IdnHost.Equals("cdn.resleriana.jp", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
        )
            return false;
        var parts = uri.AbsolutePath.Split('/');
        if (
            parts.Length is not (5 or 6)
            || parts[0] != ""
            || parts[1] != "asset"
            || !ValidVersion(parts[2])
            || parts[3] != "StandaloneWindows64"
        )
            return false;
        var name = parts[^1];
        if (parts.Length == 6 && parts[4] != "Embed")
            return false;
        if (
            !(parts.Length == 5 && name is "catalog.json" or "manifest.json")
            && !(parts.Length == 6 && name == "bundle_unitybuiltinshaders.bundle")
            && !(
                name.Length == 39
                && name.EndsWith(".bundle", StringComparison.Ordinal)
                && name.AsSpan(0, 32).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            )
        )
            return false;
        version = parts[2];
        return true;
    }

    internal static bool ValidVersion(string version)
    {
        var separator = version.IndexOf('_');
        return separator is > 0 and <= 20
            && version.Length - separator is > 1 and <= 65
            && version.AsSpan(0, separator).IndexOfAnyExcept("0123456789") < 0
            && version
                .AsSpan(separator + 1)
                .IndexOfAnyExcept("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
                < 0;
    }
}
