using System.Globalization;
using System.Threading.RateLimiting;
using Almirante.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Security;

// Rate limiting nativo do ASP.NET Core para POST /api/Auth/login, independente do nginx.
//
// Partição = HttpContext.Connection.RemoteIpAddress, lido quando o middleware executa, ou seja,
// DEPOIS de UseForwardedHeaders: só reflete X-Forwarded-For quando a conexão veio da rede
// ReverseProxy:TrustedNetworkCidr; um header enviado por cliente não confiável é ignorado.
// O contador fica em memória: é por instância da API (uma réplica = limite efetivo; N réplicas
// = até N vezes o limite). O limite global por conta é o lockout persistido (LoginLockout).
public static class LoginRateLimiting
{
    public const string PolicyName = "login";

    public static IServiceCollection AddLoginRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LoginProtectionOptions>()
            .Bind(configuration.GetSection(LoginProtectionOptions.SectionName))
            .Validate(o => o.RateLimit.PermitLimit is >= 1 and <= 10_000 && o.RateLimit.WindowSeconds is >= 1 and <= 3600,
                "LoginProtection:RateLimit inválido (PermitLimit 1-10000, WindowSeconds 1-3600).")
            .Validate(o => o.Lockout.MaxFailedAttempts is >= 1 and <= 1000 && o.Lockout.FailureWindowMinutes is >= 1 and <= 1440 &&
                           o.Lockout.LockoutMinutes is >= 1 and <= 1440,
                "LoginProtection:Lockout inválido (MaxFailedAttempts 1-1000, janelas 1-1440 minutos).")
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PolicyName, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<LoginProtectionOptions>>().Value.RateLimit;
                return RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            options.OnRejected = async (rejected, cancellationToken) =>
            {
                var response = rejected.HttpContext.Response;
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                await response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = "Muitas tentativas de login.",
                    Status = StatusCodes.Status429TooManyRequests,
                    Detail = "Aguarde antes de tentar realizar o login novamente.",
                }, options: null, contentType: "application/problem+json", cancellationToken);
            };
        });
        return services;
    }

    private static string PartitionKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return "sem-ip";
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
