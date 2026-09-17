using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Almirante.Api.Tests;

// Fábrica de testes que, ao contrário de AlmiranteApiFactory (EF Core InMemory), mantém a
// configuração REAL do DbContext (builder.AddSqlServerDbContext em Program.cs), apontando para um
// SQL Server real (container Testcontainers) via connectionString. Isso faz com que
// DbSeeder.SeedAsync rode as migrations reais (incluindo a criação do trigger
// TR_Lancamentos_AuditoriaExclusaoLogica) contra um banco de verdade — o único jeito de validar o
// trigger, SESSION_CONTEXT e o índice único sob concorrência real (ver
// LancamentosGeraisAuditoriaSqlServerTests).
//
// A configuração é passada via VARIÁVEIS DE AMBIENTE (ConnectionStrings__almirante etc, mesmo
// formato usado em produção pelo compose.yaml), não via builder.ConfigureAppConfiguration:
// confirmado por diagnóstico que, para este projeto (WebApplicationBuilder + WebApplicationFactory
// via o adaptador DeferredHostBuilder), um ConfigureAppConfiguration adicionado pela fábrica de
// testes fica visível no IConfigurationBuilder mas não chega à resolução lazy da Aspire
// (builder.AddSqlServerDbContext) no host realmente construído — variáveis de ambiente, lidas
// pelo AddEnvironmentVariables() padrão do próprio Program.cs, não têm esse problema.
public class SqlServerLancamentosGeraisFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin-sql@local.dev";
    public const string AdminSenha = "senha123";

    public SqlServerLancamentosGeraisFactory() => ClientOptions.BaseAddress = new Uri("https://localhost");

    // Só afeta variáveis de ambiente (processo inteiro) pelo tempo necessário para construir o
    // host desta fábrica; uma vez construído, o host já capturou sua própria configuração e não
    // volta a ler o ambiente, então é seguro reconfigurar o ambiente para a próxima fábrica logo
    // em seguida (ver uso em LancamentosGeraisAuditoriaSqlServerTests).
    public static SqlServerLancamentosGeraisFactory CreateAndWarmUp(string connectionString)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__almirante", connectionString);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "Almirante.Api.Tests.SqlServer");
        Environment.SetEnvironmentVariable("Jwt__Audience", "Almirante.Api.Tests.SqlServer");
        Environment.SetEnvironmentVariable("Jwt__ActiveKeyId", "test-v1");
        Environment.SetEnvironmentVariable("Jwt__Keys__test-v1", "dGVzdC1vbmx5LXNpZ25pbmcta2V5LTEyMzQ1Njc4OTAtYWJjZGVm");
        Environment.SetEnvironmentVariable("Jwt__AccessTokenMinutes", "10");
        Environment.SetEnvironmentVariable("SeedAdmin__Nome", "Administrador");
        Environment.SetEnvironmentVariable("SeedAdmin__Email", AdminEmail);
        Environment.SetEnvironmentVariable("SeedAdmin__Senha", AdminSenha);

        var factory = new SqlServerLancamentosGeraisFactory();

        // Força a construção do host agora, com o ambiente acima — não depois, quando o ambiente
        // já pode ter sido reconfigurado por outra chamada a este método.
        using var warmup = factory.CreateClient();

        return factory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
