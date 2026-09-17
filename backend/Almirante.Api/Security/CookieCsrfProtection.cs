using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;

namespace Almirante.Api.Security;

// Emissão e validação do token CSRF das operações que dependem do cookie de refresh (login,
// refresh, logout), usando o antiforgery do ASP.NET Core.
//
// O antiforgery vincula o token ao usuário autenticado em HttpContext.User. Aqui a credencial
// protegida é o cookie, não o bearer: um token emitido com bearer deixaria de valer quando o bearer
// expira ou a sessão é revogada — justamente quando a SPA precisa renovar ou sair. Por isso emissão
// e validação acontecem sempre com um principal anônimo. O par cookie + header continua exigido e a
// leitura do token por outra origem continua bloqueada pelo CORS.
public sealed class CookieCsrfProtection(IAntiforgery antiforgery)
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    public string IssueRequestToken(HttpContext context)
    {
        var user = context.User;
        context.User = Anonymous;
        try { return antiforgery.GetAndStoreTokens(context).RequestToken!; }
        finally { context.User = user; }
    }

    public async Task<bool> IsRequestValidAsync(HttpContext context)
    {
        var user = context.User;
        context.User = Anonymous;
        try { return await antiforgery.IsRequestValidAsync(context); }
        finally { context.User = user; }
    }
}
