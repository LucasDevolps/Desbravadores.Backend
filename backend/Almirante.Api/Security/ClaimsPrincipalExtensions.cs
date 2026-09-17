using System.Security.Claims;

namespace Almirante.Api.Security;

public static class ClaimsPrincipalExtensions
{
    // Extrai o id do usuário autenticado da claim ClaimTypes.NameIdentifier. O JWT emite apenas
    // "sub"; o mapeamento sub -> NameIdentifier é feito explicitamente em Program.cs
    // (JwtBearerEvents.OnTokenValidated), depois de validar que "sub" é um GUID único. Não há
    // fallback para "sub" aqui de propósito: uma regressão nesse mapeamento deve falhar (401) em vez
    // de passar despercebida.
    public static bool TentarObterUsuarioId(this ClaimsPrincipal principal, out Guid usuarioId) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out usuarioId);
}
