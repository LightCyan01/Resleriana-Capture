using System.IO.Compression;
using System.Security.Cryptography;

namespace AtelierCapture;

internal sealed record PreservedBody(
    string request_id,
    string attempt_id,
    string? operation_id,
    string? identity_proof,
    string? response_id,
    string route,
    string direction,
    string stage,
    string body_id,
    byte[] body,
    string sha256,
    string? native_operation_id = null,
    string? logical_route = null,
    string? transport_path = null,
    string? request_header_id = null,
    string? descriptor_id = null
)
{
    public int bytes => body.Length;
}

internal static class ApiBodies
{
    internal static PreservedBody Detach(
        NetworkContext context,
        string direction,
        string stage,
        byte[] bytes
    )
    {
        var detached = (byte[])bytes.Clone();
        return Own(
            context,
            direction,
            stage,
            detached,
            Convert.ToHexString(SHA256.HashData(detached))
        );
    }

    internal static PreservedBody Own(
        NetworkContext context,
        string direction,
        string stage,
        byte[] detachedBytes,
        string sha256
    ) =>
        new(
            context.RequestId,
            context.AttemptId,
            context.OperationId,
            context.IdentityProof,
            context.ResponseId,
            context.Route,
            direction,
            stage,
            context.AttemptId + ":" + direction + ":" + stage + ":" + sha256,
            detachedBytes,
            sha256,
            context.NativeOperationId,
            context.LogicalRoute,
            context.TransportPath,
            context.RequestHeaderId,
            context.DescriptorId
        );

    internal static byte[] Expand(byte[] compressed)
    {
        using var gzip = new GZipStream(
            new MemoryStream(compressed, writable: false),
            CompressionMode.Decompress
        );
        using var expanded = new MemoryStream();
        gzip.CopyTo(expanded, 65536);
        return expanded.ToArray();
    }
}
