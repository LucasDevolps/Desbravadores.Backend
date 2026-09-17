using Almirante.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

public class AlmiranteApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    public const string AdminEmail = "admin@local.dev";
    public const string AdminSenha = "senha123";
    public const string JwtIssuer = "Almirante.Api.Tests";
    public const string JwtAudience = "Almirante.Api.Tests";
    public const string JwtKeyId = "test-v1";
    // Somente para testes: 32 bytes aleatórios (CSPRNG) em Base64, no mesmo formato de produção.
    public const string JwtKeyBase64 = "G66fed02cU3xa0os0mFnvIWqOY3LtEw2IJ+EnMDGJRk=";

    public AlmiranteApiFactory() => ClientOptions.BaseAddress = new Uri("https://localhost");

    // Cliente sem CookieContainer, para os testes que controlam cookies explicitamente (CookieJar).
    public HttpClient CreateManualCookieClient(string baseAddress = "https://localhost") =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(baseAddress) });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:almirante"] = "Server=localhost;Database=ignored;Trusted_Connection=True;",
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["Jwt:ActiveKeyId"] = JwtKeyId,
                [$"Jwt:Keys:{JwtKeyId}"] = JwtKeyBase64,
                ["Jwt:AccessTokenMinutes"] = "10",
                ["SeedAdmin:Nome"] = "Administrador",
                ["SeedAdmin:Email"] = AdminEmail,
                ["SeedAdmin:Senha"] = AdminSenha,
            });
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

            services.AddDbContext<AlmiranteDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
