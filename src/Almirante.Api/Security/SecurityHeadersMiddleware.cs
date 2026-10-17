using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment, IOptions<HstsOptions> hstsOptions, IConfiguration configuration)
{
    public const string ApiContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public const string SwaggerContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; " +
        "font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private readonly bool hstsInDevelopment = configuration.GetValue<bool>("Hsts:EnabledInDevelopment");

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

        if ((!environment.IsDevelopment() || hstsInDevelopment) && context.Request.IsHttps && !IsExcludedHost(context.Request.Host.Host))
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
