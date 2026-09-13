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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:almirante"] = "Server=localhost;Database=ignored;Trusted_Connection=True;",
                ["Jwt:Issuer"] = "Almirante.Api.Tests",
                ["Jwt:Audience"] = "Almirante.Api.Tests",
                ["Jwt:Key"] = "test-only-signing-key-1234567890-abcdef",
                ["Jwt:ExpirationMinutes"] = "60",
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
