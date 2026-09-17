using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

// IAuthorizationMiddlewareResultHandler vive em Microsoft.AspNetCore.Authorization; a
// implementação padrão do framework (AuthorizationMiddlewareResultHandler), usada aqui como
// fallback via _default, vive em Microsoft.AspNetCore.Authorization.Policy — daí os dois usings.

namespace Almirante.Api.Security;

// Único IAuthorizationMiddlewareResultHandler registrado na aplicação.
// - Sem autenticação válida (sem token, token inválido/expirado, sessão revogada): comportamento
//   padrão -> challenge do JwtBearer, 401 com WWW-Authenticate.
// - Autenticado sem permissão (policy/role não satisfeita): 403 Problem Details "ACESSO NEGADO!".
//   Não usar 401 aqui: o cliente trataria como sessão inválida e descartaria a autenticação.
public class AcessoNegadoAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private const string MensagemAcessoNegado = "ACESSO NEGADO!";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails { Title = MensagemAcessoNegado, Status = StatusCodes.Status403Forbidden },
                options: null,
                contentType: "application/problem+json");
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
