using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Almirante.Api.Security;

// Perfil do access token deste projeto (não é exigência da RFC 7519 para todo JWT).
public static class JwtProfile
{
    public const string TokenType = "at+jwt";
    public const string RoleClaim = "role";
    public const string SessionClaim = "sid";

    // Limite aplicado pelo JsonWebTokenHandler antes de decodificar o token.
    public const int MaximumTokenSizeInBytes = 8192;

    public static readonly string[] RequiredClaims = ["sub", RoleClaim, "iat", "nbf", "exp", "jti", SessionClaim];

    private static readonly string[] NumericDateClaims = ["exp", "iat", "nbf"];

    private static readonly HashSet<string> CriticalClaims = new(StringComparer.Ordinal)
    {
        "iss", "aud", "sub", RoleClaim, "iat", "nbf", "exp", "jti", SessionClaim,
    };

    // Verificação estrutural limitada do payload JÁ validado criptograficamente: o parser da
    // biblioteca aceita chaves duplicadas (vale a última) e datas como string numérica. Aqui uma claim
    // crítica duplicada ou uma data que não seja NumericDate inteiro invalida o token.
    public static bool HasUnambiguousPayload(JsonWebToken token)
    {
        try
        {
            var reader = new Utf8JsonReader(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                if (CriticalClaims.Contains(name) && !seen.Add(name)) return false;
                if (!reader.Read()) return false;
                if (NumericDateClaims.Contains(name) && (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out _)))
                    return false;
                reader.Skip();
            }

            return reader.TokenType == JsonTokenType.EndObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
