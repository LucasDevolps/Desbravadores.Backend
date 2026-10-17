using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Security;

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
