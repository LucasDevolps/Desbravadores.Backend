using System.Security.Claims;

namespace Almirante.Api.Security;

public static class ClaimsPrincipalExtensions
{
    public static bool TentarObterUsuarioId(this ClaimsPrincipal principal, out Guid usuarioId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out usuarioId);
}
