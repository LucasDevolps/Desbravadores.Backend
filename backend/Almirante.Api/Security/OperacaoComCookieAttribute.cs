using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Almirante.Api.Security;

// Marca endpoints que emitem ou consomem os cookies __Host- de autenticação.
// - Fora de HTTPS responde 400 (ProblemDetails) sem tocar em cookies: cookies Secure/__Host- não
//   funcionam em HTTP e o antiforgery lançaria exceção (500). Atrás de proxy, o esquema vem do
//   X-Forwarded-Proto aceito pelo ForwardedHeadersMiddleware.
// - Com ValidarCsrf, exige o par cookie + header X-CSRF-TOKEN válido antes do model binding.
[AttributeUsage(AttributeTargets.Method)]
public sealed class OperacaoComCookieAttribute : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    public bool ValidarCsrf { get; init; }

    public int Order => int.MinValue;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (!http.Request.IsHttps)
        {
            context.Result = Problem("HTTPS obrigatório.", "Esta operação usa cookies seguros e só é aceita por HTTPS.");
            return;
        }

        if (ValidarCsrf && !await http.RequestServices.GetRequiredService<CookieCsrfProtection>().IsRequestValidAsync(http))
            context.Result = Problem("Proteção CSRF inválida.", "Obtenha um token em GET /api/Auth/csrf e envie-o no header X-CSRF-TOKEN.");
    }

    private static ObjectResult Problem(string title, string detail) =>
        new(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status400BadRequest })
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
}
