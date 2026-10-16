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
        var expires = new DateTimeOffset(absoluteExpiry, TimeSpan.Zero) < now.AddMinutes(_options.AccessTokenMinutes)
            ? new DateTimeOffset(absoluteExpiry, TimeSpan.Zero) : now.AddMinutes(_options.AccessTokenMinutes);
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
    // HS256 (RFC 7518, seção 3.2) exige chave de tamanho >= saída do hash: 256 bits = 32 bytes do
    // material DECODIFICADO. O valor configurado é Base64, então 32 caracteres não bastam.
    public const int MinimumKeyBytes = 32;

    public static SymmetricSecurityKey GetKey(JwtOptions options, string kid)
    {
        if (!options.Keys.TryGetValue(kid, out var encoded)) throw new SecurityTokenInvalidSigningKeyException("kid desconhecido.");
        if (!TryDecode(encoded, out var bytes, out var error))
            throw new OptionsValidationException(JwtOptions.SectionName, typeof(JwtOptions), [$"Jwt:Keys:{kid}: {error}"]);
        return new SymmetricSecurityKey(bytes) { KeyId = kid };
    }

    // Nunca inclui o valor da chave na mensagem de erro (ela vai para log/exceção de startup).
    // Só é possível verificar formato e tamanho; aleatoriedade não é verificável aqui — por isso a
    // documentação exige gerar a chave com CSPRNG (ex.: openssl rand -base64 32). A checagem de bytes
    // repetidos só barra o caso trivial (ex.: "AAAA...=" = 32 zeros), não prova entropia.
    public static bool TryDecode(string? encoded, out byte[] bytes, out string error)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(encoded)) { error = "chave ausente."; return false; }
        if (SecretPlaceholders.IsPlaceholder(encoded)) { error = "chave ainda é um placeholder; gere uma chave real fora do Git."; return false; }
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { error = "chave não é Base64 válida."; return false; }
        if (bytes.Length < MinimumKeyBytes)
        {
            error = $"chave decodificada possui {bytes.Length} bytes; HS256 exige ao menos {MinimumKeyBytes} bytes.";
            return false;
        }
        if (bytes.Distinct().Count() == 1) { error = "chave trivial (todos os bytes iguais)."; return false; }
        error = "";
        return true;
    }
}

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer)) failures.Add("Jwt:Issuer é obrigatório.");
        if (string.IsNullOrWhiteSpace(options.Audience)) failures.Add("Jwt:Audience é obrigatório.");
        if (options.Keys.Count == 0)
            failures.Add("Jwt:Keys está vazio. Configure ao menos uma chave (ex.: Jwt__Keys__v1) via user-secrets, variável de ambiente ou cofre.");
        if (string.IsNullOrWhiteSpace(options.ActiveKeyId) || !options.Keys.ContainsKey(options.ActiveKeyId))
            failures.Add("Jwt:ActiveKeyId deve referenciar uma chave existente em Jwt:Keys.");
        foreach (var (kid, value) in options.Keys)
        {
            if (!JwtKeySet.TryDecode(value, out _, out var error)) failures.Add($"Jwt:Keys:{kid}: {error}");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
