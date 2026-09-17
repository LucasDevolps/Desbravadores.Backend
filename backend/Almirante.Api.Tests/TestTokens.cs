using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Almirante.Api.Tests;

// Emite/manipula JWTs com a chave de teste da AlmiranteApiFactory para os cenários negativos
// (expirado, algoritmo inesperado, claims duplicadas...). A validação continua sendo a real da API.
public static class TestTokens
{
    public static byte[] Key => Convert.FromBase64String(AlmiranteApiFactory.JwtKeyBase64);

    // Recria o token com o mesmo payload, aplicando alterações, e assina com a chave de teste.
    public static string Resign(string accessToken, Action<Dictionary<string, object?>>? payload = null,
        Action<Dictionary<string, object?>>? header = null, string algorithm = "HS256", byte[]? key = null)
    {
        var token = new JsonWebToken(accessToken);
        var headerValues = ReadObject(token.EncodedHeader);
        var payloadValues = ReadObject(token.EncodedPayload);
        headerValues["alg"] = algorithm;
        header?.Invoke(headerValues);
        payload?.Invoke(payloadValues);
        return SignRaw(System.Text.Json.JsonSerializer.Serialize(headerValues), System.Text.Json.JsonSerializer.Serialize(payloadValues), algorithm, key);
    }

    // Assina um header/payload JSON literal (permite, por exemplo, chaves duplicadas).
    public static string SignRaw(string headerJson, string payloadJson, string algorithm = "HS256", byte[]? key = null)
    {
        var signingInput = $"{Base64UrlEncoder.Encode(headerJson)}.{Base64UrlEncoder.Encode(payloadJson)}";
        var bytes = Encoding.ASCII.GetBytes(signingInput);
        byte[] signature = algorithm switch
        {
            "HS512" => System.Security.Cryptography.HMACSHA512.HashData(key ?? Key, bytes),
            _ => System.Security.Cryptography.HMACSHA256.HashData(key ?? Key, bytes),
        };
        return $"{signingInput}.{Base64UrlEncoder.Encode(signature)}";
    }

    public static string Expired(string accessToken, int secondsAgo = 300) => Resign(accessToken, p =>
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        p["iat"] = now - 900;
        p["nbf"] = now - 900;
        p["exp"] = now - secondsAgo;
    });

    public static Dictionary<string, object?> ReadObject(string base64Url) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(Base64UrlEncoder.Decode(base64Url))!;
}
