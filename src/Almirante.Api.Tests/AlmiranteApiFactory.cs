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

    public static readonly string AdminSenha = $"Tst-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}-ok";
    public string JwtKeyId => "test-v1";
    public string JwtKeyBase64 { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public const string TestRemoteIpHeader = "X-Test-Remote-Ip";
    public const string ClaimsPath = "/__tests/claims";
    public const string ThrowPath = "/__tests/throw";

    public TestLogSink Logs { get; } = new();

    public ViaCepFalso ViaCep { get; } = new();

    public string FotosPath { get; } = Path.Combine(Path.GetTempPath(), "almirante-testes-fotos", Guid.NewGuid().ToString("N"));

    /// <summary>Diário da auditoria HTTP desta factory: o padrão é derivado da connection string, igual em todas as factories em memória.</summary>
    public string AuditoriaPath { get; } = Path.Combine(Path.GetTempPath(), "almirante-testes-auditoria", Guid.NewGuid().ToString("N"));

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
                ["LoginProtection:RateLimit:PermitLimit"] = "10000",
                ["LoginProtection:Lockout:MaxFailedAttempts"] = "1000",
                ["Senhas:TentativasPorUsuario"] = "10000",
                ["Senhas:TentativasPorIp"] = "10000",
                ["Senhas:RecuperacoesPorIp"] = "10000",
                ["Media:Storage:Path"] = FotosPath,
                ["AuditoriaHttp:DiretorioPendentes"] = AuditoriaPath,
                ["ViaCep:TimeoutMs"] = "600",
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
            services.AddHttpClient(Almirante.Api.Services.ViaCepClient.NomeHttpClient).ConfigurePrimaryHttpMessageHandler(() => ViaCep);
            ConfigureTestServices(services);
        });
    }

    protected virtual void ConfigureTestServices(IServiceCollection services) { }

    protected virtual void ConfigureDatabase(IServiceCollection services) =>
        services.AddDbContext<AlmiranteDbContext>(options => options.UseInMemoryDatabase(_databaseName));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            ApagarPastaTemporaria(FotosPath);
            ApagarPastaTemporaria(AuditoriaPath);
        }
    }

    // Melhor esforço, como em ApiProcess: no Windows um arquivo ainda aberto (antivírus, indexador) faz o Delete lançar
    // IOException, e uma sobra na pasta temporária não pode reprovar a classe de testes inteira.
    private static void ApagarPastaTemporaria(string pasta)
    {
        try { if (Directory.Exists(pasta)) Directory.Delete(pasta, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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
