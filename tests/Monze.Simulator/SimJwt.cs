using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Monze.Simulator;

/// <summary>
/// Builds the session JWTs the simulated gateway issues. Mezon.Net 1.6.2
/// decodes them without validating the signature
/// (src/Mezon.Net.Client/Session/Session.cs reads the "exp", "uid" and "usn"
/// claims of the token and "exp" of the refresh token), so an HS256 token
/// signed with a per-simulator random key is enough.
/// </summary>
internal static class SimJwt
{
    private static readonly byte[] Header = Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}""");

    public static string Create(IReadOnlyDictionary<string, object> claims, byte[] key)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        var signingInput = Base64Url(Header) + "." + Base64Url(payload);
        var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput));
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
