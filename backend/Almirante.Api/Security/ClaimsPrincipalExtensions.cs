using System.Security.Claims;

namespace Almirante.Api.Security;

public static class ClaimsPrincipalExtensions
{
    // Extrai o id do usuário autenticado (claim ClaimTypes.NameIdentifier, emitida em
    // JwtTokenService a partir de Usuario.Id). Reaproveitado por AuthController.Me,
    // LancamentosController e LancamentosGeraisController, que antes tinham cada um sua própria
    // cópia deste parse.
    public static bool TentarObterUsuarioId(this ClaimsPrincipal principal, out Guid usuarioId)
    {
        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(idClaim, out usuarioId);
    }
}
