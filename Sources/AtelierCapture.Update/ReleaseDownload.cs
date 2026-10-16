using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AtelierCapture.Update;

internal sealed record ReleaseAsset(string Version, Uri Url, long Size, string Sha256);

internal sealed class ReleaseDownload(HttpClient client)
{
    internal const string LatestUrl =
        "https://api.github.com/repos/" + UpdateFiles.Repository + "/releases/latest";

    internal async Task<ReleaseAsset?> Latest(Version current, CancellationToken cancellation)
    {
        using var response = await Send(
            new Uri(LatestUrl),
            "application/vnd.github+json",
            cancellation
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var bytes = new MemoryStream();
        await CopyBounded(input, bytes, UpdateFiles.MetadataLimit, cancellation);
        using var data = JsonDocument.Parse(bytes.ToArray());
        return Parse(data.RootElement, current);
    }

    internal static ReleaseAsset? Parse(JsonElement release, Version current)
    {
        if (
            release.GetProperty("draft").GetBoolean()
            || release.GetProperty("prerelease").GetBoolean()
            || release.GetProperty("published_at").ValueKind != JsonValueKind.String
        )
            return null;
        var tag = release.GetProperty("tag_name").GetString()!;
        var version = UpdateFiles.Version(tag.StartsWith('v') ? tag[1..] : tag);
        if (version <= current)
            return null;
        var name = $"Resleriana-Capture-v{version}-update-win-x64.zip";
        var assets = release
            .GetProperty("assets")
            .EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == name)
            .ToArray();
        if (assets.Length == 0)
            return null;
        if (assets.Length != 1)
            throw new InvalidDataException("Duplicate release update assets.");
        var asset = assets[0];
        var digest = asset.GetProperty("digest").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        var expected =
            $"https://github.com/{UpdateFiles.Repository}/releases/download/{tag}/{name}";
        if (
            asset.GetProperty("state").GetString() != "uploaded"
            || size is < 1 or > 32 * 1024 * 1024
            || digest is null
            || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || asset.GetProperty("browser_download_url").GetString() != expected
        )
            throw new InvalidDataException("Unrecognized published update asset.");
        UpdateFiles.ValidateStamp(new(name, size, digest[7..]));
        return new(version.ToString(), new Uri(expected), size, digest[7..]);
    }

    internal async Task Download(ReleaseAsset asset, string path, CancellationToken cancellation)
    {
        using var response = await Send(asset.Url, "application/octet-stream", cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            throw new InvalidDataException("Update download length differs from GitHub.");
        using (
            var output = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                65536,
                true
            )
        )
        {
            using var input = await response.Content.ReadAsStreamAsync(cancellation);
            await CopyBounded(input, output, asset.Size, cancellation);
            await output.FlushAsync(cancellation);
            output.Flush(true);
        }
        if (!UpdateFiles.Matches(path, new("download", asset.Size, asset.Sha256)))
            throw new InvalidDataException("Downloaded update digest differs from GitHub.");
    }

    private async Task<HttpResponseMessage> Send(
        Uri uri,
        string accept,
        CancellationToken cancellation
    )
    {
        for (var count = 0; count < 6; count++)
        {
            if (!Allowed(uri))
                throw new InvalidDataException("Untrusted update download redirect.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Resleriana-Capture-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (uri.Host == "api.github.com")
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation
            );
            if (
                response.StatusCode
                is not (
                    HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect
                )
            )
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidDataException("Update redirect has no location.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("Too many update redirects.");
    }

    internal static bool Allowed(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == "https"
        && uri.Port == 443
        && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0
        && uri.Host
            is "github.com"
                or "api.github.com"
                or "release-assets.githubusercontent.com"
                or "objects.githubusercontent.com";

    internal static async Task CopyBounded(
        Stream source,
        Stream target,
        long maximum,
        CancellationToken cancellation
    )
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellation)) != 0)
        {
            total = checked(total + count);
            if (total > maximum)
                throw new InvalidDataException("Update response exceeded its size limit.");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
    }
}
