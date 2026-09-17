using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Almirante.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Almirante.Api.Security;

public record IssuedToken(string AccessToken, DateTime ExpiresAtUtc);

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public IssuedToken GenerateToken(Guid userId, string role, Guid sessionId, DateTime absoluteExpiry)
    {
        var now = clock.GetUtcNow();
        // exp é NumericDate em segundos: arredonda para baixo para que expiresAtUtc seja exatamente o
        // exp emitido e o token nunca ultrapasse o limite absoluto da sessão.
        var absolute = new DateTimeOffset(DateTime.SpecifyKind(absoluteExpiry, DateTimeKind.Utc));
        var expires = DateTimeOffset.FromUnixTimeSeconds(Math.Min(
            absolute.ToUnixTimeSeconds(), now.AddMinutes(_options.AccessTokenMinutes).ToUnixTimeSeconds()));
        var key = JwtKeySet.GetKey(_options, _options.ActiveKeyId);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer, Audience = _options.Audience,
            NotBefore = now.UtcDateTime, IssuedAt = now.UtcDateTime, Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity([
                new(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new("role", role), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new("sid", sessionId.ToString())]),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            TokenType = "at+jwt",
        };
        descriptor.SigningCredentials.Key.KeyId = _options.ActiveKeyId;
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return new(handler.WriteToken(handler.CreateToken(descriptor)), expires.UtcDateTime);
    }
}

public static class JwtKeySet
{
    public static SymmetricSecurityKey GetKey(JwtOptions options, string kid)
    {
        if (!options.Keys.TryGetValue(kid, out var encoded)) throw new SecurityTokenInvalidSigningKeyException("kid desconhecido.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); } catch (FormatException) { throw new OptionsValidationException(JwtOptions.SectionName, typeof(JwtOptions), ["Chave JWT não é Base64 válida."]); }
        if (bytes.Length < 32) throw new OptionsValidationException(JwtOptions.SectionName, typeof(JwtOptions), ["Chave JWT deve possuir ao menos 32 bytes."]);
        return new SymmetricSecurityKey(bytes) { KeyId = kid };
    }
}
