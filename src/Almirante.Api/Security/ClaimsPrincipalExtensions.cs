using System.Security.Claims;
using Almirante.Api.Infrastructure;

namespace Almirante.Api.Security;

public static class ClaimsPrincipalExtensions
{
    public static bool TentarObterUsuarioId(this ClaimsPrincipal principal, out Guid usuarioId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out usuarioId);

    /// <summary>
    /// Sessão (claim sid) de um token já aceito pelo JwtBearer (OnTokenValidated exige sid GUID e sessão vigente); sem ela, 401 como em
    /// <see cref="UsuarioId"/>. Usada nas operações de senha para conferir e travar a sessão de quem confirma a operação.
    /// </summary>
    public static Guid SessaoId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sid"), out var sessaoId)
            ? sessaoId
            : throw new ApiProblemException(StatusCodes.Status401Unauthorized, "Identidade autenticada inválida.");

    /// <summary>
    /// Id do usuário de um token já aceito pelo JwtBearer (OnTokenValidated exige sub GUID e sessão vigente). Usado no autoatendimento,
    /// onde a identidade nunca vem do cliente; sem ele a requisição é recusada (401, como nos demais controllers) em vez de agir sobre
    /// outro usuário.
    /// </summary>
    public static Guid UsuarioId(this ClaimsPrincipal principal) =>
        principal.TentarObterUsuarioId(out var usuarioId)
            ? usuarioId
            : throw new ApiProblemException(StatusCodes.Status401Unauthorized, "Identidade autenticada inválida.");

    /// <summary>Cargo (role) do token validado, conferido com o banco a cada requisição; vazio sem autenticação.</summary>
    public static string Role(this ClaimsPrincipal principal) => principal.FindFirstValue("role") ?? string.Empty;
}
