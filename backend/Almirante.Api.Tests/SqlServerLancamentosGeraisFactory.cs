using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Almirante.Api.Tests;

// Fábrica de testes que, ao contrário de AlmiranteApiFactory (EF Core InMemory), mantém a
// configuração REAL do DbContext (builder.AddSqlServerDbContext em Program.cs), apontando para um
// SQL Server real (container Testcontainers) via connectionString. Isso faz com que
// DbSeeder.SeedAsync rode as migrations reais (incluindo a criação do trigger
// TR_Lancamentos_AuditoriaExclusaoLogica) contra um banco de verdade — o único jeito de validar o
// trigger, SESSION_CONTEXT e o índice único sob concorrência real (ver
// LancamentosGeraisAuditoriaSqlServerTests).
public class SqlServerLancamentosGeraisFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin-sql@local.dev";
    public const string AdminSenha = "senha123";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:almirante"] = connectionString,
                ["Jwt:Issuer"] = "Almirante.Api.Tests.SqlServer",
                ["Jwt:Audience"] = "Almirante.Api.Tests.SqlServer",
                ["Jwt:Key"] = "test-only-signing-key-1234567890-abcdef",
                ["Jwt:ExpirationMinutes"] = "60",
                ["SeedAdmin:Nome"] = "Administrador",
                ["SeedAdmin:Email"] = AdminEmail,
                ["SeedAdmin:Senha"] = AdminSenha,
            });
        });
    }
}
