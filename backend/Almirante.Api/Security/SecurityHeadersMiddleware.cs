using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Security;

// Cabeçalhos de segurança aplicados pela própria aplicação (o nginx não os adiciona às respostas
// da API, evitando duplicidade). Os valores são gravados em Response.OnStarting, logo alcançam
// TODAS as respostas — inclusive as geradas por UseExceptionHandler (que limpa os headers antes de
// escrever o erro), challenge/forbid da autenticação/autorização, 404 e 429 do rate limiter.
//
// HSTS é emitido aqui (e não por app.UseHsts()) pelo mesmo motivo: o HstsMiddleware grava o header
// antes de chamar o próximo middleware e ele seria apagado em respostas de erro. Os parâmetros vêm
// de HstsOptions (seção "Hsts"). Regras do framework preservadas: nunca em Development, só quando a
// requisição é HTTPS — considerando X-Forwarded-Proto apenas de proxy confiável — e nunca para os
// hosts excluídos (localhost, 127.0.0.1, [::1] por padrão).
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment, IOptions<HstsOptions> hstsOptions)
{
    // API JSON: nada deve ser carregado, embutido em frame ou usado como base/form.
    public const string ApiContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    // Swagger UI (Swashbuckle): scripts, estilos e imagens servidos pela própria origem; o
    // documento OpenAPI é obtido via fetch (connect-src 'self'). Scripts sem 'unsafe-inline' nem
    // 'unsafe-eval'. Estilos aceitam 'unsafe-inline' porque o swagger-ui injeta estilo em runtime
    // (verificado no navegador); CSS inline não executa código e vale só para /swagger.
    public const string SwaggerContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; " +
        "font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (httpContext, middleware) = ((HttpContext, SecurityHeadersMiddleware))state;
            middleware.Apply(httpContext);
            return Task.CompletedTask;
        }, (context, this));
        return next(context);
    }

    private void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase)
            ? SwaggerContentSecurityPolicy
            : ApiContentSecurityPolicy;

        if (!environment.IsDevelopment() && context.Request.IsHttps && !IsExcludedHost(context.Request.Host.Host))
        {
            var options = hstsOptions.Value;
            var value = $"max-age={(long)options.MaxAge.TotalSeconds}";
            if (options.IncludeSubDomains) value += "; includeSubDomains";
            if (options.Preload) value += "; preload";
            headers.StrictTransportSecurity = value;
        }
    }

    private bool IsExcludedHost(string host) =>
        hstsOptions.Value.ExcludedHosts.Any(excluded => string.Equals(excluded, host, StringComparison.OrdinalIgnoreCase));
}
