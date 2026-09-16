using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

// IAuthorizationMiddlewareResultHandler vive em Microsoft.AspNetCore.Authorization; a
// implementação padrão do framework (AuthorizationMiddlewareResultHandler), usada aqui como
// fallback via _default, vive em Microsoft.AspNetCore.Authorization.Policy — daí os dois usings.

namespace Almirante.Api.Security;

// Único IAuthorizationMiddlewareResultHandler registrado na aplicação (substitui o handler
// padrão do framework). Para a maioria dos endpoints, delega 100% para o comportamento padrão
// (_default) — nada muda. Só intercepta quando o endpoint carrega a policy
// LancamentoGeralAuthorization.PolicyName, caso em que qualquer falha de autorização (sem token,
// token inválido/expirado, ou autenticado sem uma das roles permitidas) vira 401 com a mensagem
// exata exigida pelo requisito, em vez do 401 "silencioso" (sem token) ou 403 (sem role) padrão.
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
        if (!authorizeResult.Succeeded && AplicaEscopoLancamentoGeral(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = MensagemAcessoNegado,
                Status = StatusCodes.Status401Unauthorized,
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static bool AplicaEscopoLancamentoGeral(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        return endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(a => a.Policy == LancamentoGeralAuthorization.PolicyName);
    }
}
