using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AtelierCapture;

internal sealed class Privacy
{
    private readonly byte[] salt = RandomNumberGenerator.GetBytes(32);
    private static readonly HashSet<string> Identifiers = new(StringComparer.Ordinal)
    {
        "userid", "accountid", "steamid", "transactionid", "orderid", "friendid", "invitationcode"
    };

    internal JsonNode? Clean(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                if (Identifiers.Contains(normalized))
                    obj[key] = "id-" + Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(obj[key]?.ToJsonString() ?? "null")))[..16];
                else if (Sensitive(normalized))
                    obj[key] = "[redacted]";
                else
                    Clean(obj[key]);
            }
        }
        else if (value is JsonArray array)
        {
            foreach (var item in array) Clean(item);
        }
        return value;
    }

    internal static bool Sensitive(string key) =>
        key.Contains("token", StringComparison.Ordinal) || key.Contains("secret", StringComparison.Ordinal)
        || key.Contains("password", StringComparison.Ordinal) || key.Contains("receipt", StringComparison.Ordinal)
        || key.StartsWith("device", StringComparison.Ordinal) || key.Contains("authorization", StringComparison.Ordinal)
        || key is "name" or "nickname" or "memo" or "message" or "messages" or "email" or "advertisingid"
            or "accesskey" or "cookie" or "nonce" or "ipaddress" or "signature" or "transfercode"
        || key.EndsWith("url", StringComparison.Ordinal) || key.EndsWith("urls", StringComparison.Ordinal);

    internal static JsonNode? Parse(string json) => JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 128 });
}
