using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Almirante.Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Tests;

public class AlmiranteApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    public const string AdminEmail = "admin@local.dev";

    // Credenciais descartáveis geradas a cada execução: nada utilizável fica versionado.
    public static readonly string AdminSenha = $"Tst-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}-ok";
    public string JwtKeyId => "test-v1";
    // 64 bytes: atende HS256 e também permite forjar HS384/HS512 com a MESMA chave nos testes de algoritmo.
    public string JwtKeyBase64 { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    // Cabeçalho SÓ de teste: simula o IP da conexão TCP imediata (TestServer não tem socket).
    // Aplicado antes de todo o pipeline real, ou seja, antes de UseForwardedHeaders.
    public const string TestRemoteIpHeader = "X-Test-Remote-Ip";
    public const string ClaimsPath = "/__tests/claims";
    public const string ThrowPath = "/__tests/throw";

    public TestLogSink Logs { get; } = new();

    // Sobrescritas por fábricas derivadas/testes específicos (limites baixos, ambiente, proxy...).
    protected virtual string EnvironmentName => "Development";
    protected virtual void ConfigureSettings(IDictionary<string, string?> settings) { }

    public AlmiranteApiFactory() => ClientOptions.BaseAddress = new Uri("https://localhost");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:almirante"] = "Server=localhost;Database=ignored;Trusted_Connection=True;",
                ["Jwt:Issuer"] = "Almirante.Api.Tests",
                ["Jwt:Audience"] = "Almirante.Api.Tests",
                ["Jwt:ActiveKeyId"] = JwtKeyId,
                [$"Jwt:Keys:{JwtKeyId}"] = JwtKeyBase64,
                ["Jwt:AccessTokenMinutes"] = "10",
                ["SeedAdmin:Nome"] = "Administrador",
                ["SeedAdmin:Email"] = AdminEmail,
                ["SeedAdmin:Senha"] = AdminSenha,
                // Suíte funcional faz muitos logins pelo mesmo "IP"; os limites reais são testados
                // em LoginProtectionTests com valores baixos.
                ["LoginProtection:RateLimit:PermitLimit"] = "10000",
                ["LoginProtection:Lockout:MaxFailedAttempts"] = "1000",
            };
            ConfigureSettings(settings);
            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });

        builder.ConfigureServices(services =>
        {
            // Aspire's AddSqlServerDbContext registers a pooled DbContext (singleton pool + scoped
            // options/lease). Every descriptor tied to AlmiranteDbContext must be removed before
            // re-registering it as a plain (non-pooled) context over the InMemory provider, otherwise
            // the singleton pool ends up depending on the newly-scoped options and DI validation fails.
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(AlmiranteDbContext)
                    || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(AlmiranteDbContext))))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            ConfigureDatabase(services);
            services.AddSingleton<IStartupFilter, TestEndpointsStartupFilter>();
            ConfigureTestServices(services);
        });
    }

    protected virtual void ConfigureTestServices(IServiceCollection services) { }

    protected virtual void ConfigureDatabase(IServiceCollection services) =>
        services.AddDbContext<AlmiranteDbContext>(options => options.UseInMemoryDatabase(_databaseName));

    private sealed class TestEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestRemoteIpHeader, out var ip))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                }
                return nextMiddleware(context);
            });

            next(app);

            // Depois do pipeline real: autenticação já executou (User preenchido) e exceções passam
            // pelo UseExceptionHandler da aplicação.
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path == ClaimsPath)
                {
                    await context.Response.WriteAsJsonAsync(new
                    {
                        authenticated = context.User.Identity?.IsAuthenticated ?? false,
                        name = context.User.Identity?.Name,
                        isAdmin = context.User.IsInRole("ADM"),
                        claims = context.User.Claims.Select(c => new { c.Type, c.Value }),
                    });
                    return;
                }
                if (context.Request.Path == ThrowPath) throw new InvalidOperationException("falha simulada de teste");
                await nextMiddleware(context);
            });
        };
    }
}

// Fábrica configurável por teste, sem precisar criar uma subclasse para cada combinação.
public sealed class ConfiguredApiFactory(string environment = "Development", IDictionary<string, string?>? overrides = null,
    Action<IServiceCollection>? services = null)
    : AlmiranteApiFactory
{
    protected override string EnvironmentName => environment;

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>()) settings[key] = value;
    }

    protected override void ConfigureTestServices(IServiceCollection serviceCollection) => services?.Invoke(serviceCollection);
}

public sealed class TestLogSink : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, Entries);
    public void Dispose() { }

    private sealed class SinkLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {structured} {exception}");
        }
    }
}
