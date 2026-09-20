using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

// Exige um SQL Server real. Defina ALMIRANTE_TEST_SQLSERVER com uma connection string SEM Database
// (ex.: "Server=localhost;Trusted_Connection=True;TrustServerCertificate=True"); cada fábrica cria um
// banco descartável almirante_test_<guid>, aplica TODAS as migrations pelo startup real e o remove
// no Dispose. Excluídos do CI (runner sem SQL Server) pelo filtro Category!=RequiresSqlServer.
public sealed class SqlServerApiFactory(IDictionary<string, string?>? overrides = null, Action<DbContextOptionsBuilder>? configureDb = null) : AlmiranteApiFactory
{
    public const string EnvironmentVariable = "ALMIRANTE_TEST_SQLSERVER";
    private readonly string _connectionString = BuildConnectionString();

    // Exposta para testes que precisam de um AlmiranteDbContext cru, antes de subir a
    // WebApplicationFactory (que aplica todas as migrations no boot) — ver LancamentosAuditMigrationTests.
    // internal (não protected): a classe é sealed, então não há subclasse para herdar o acesso.
    internal string ConnectionString => _connectionString;

    private static string BuildConnectionString()
    {
        var server = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(server))
            throw new InvalidOperationException($"Defina {EnvironmentVariable} para executar testes Category=RequiresSqlServer.");
        return new SqlConnectionStringBuilder(server) { InitialCatalog = $"almirante_test_{Guid.NewGuid():N}" }.ConnectionString;
    }

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>()) settings[key] = value;
    }

    protected override void ConfigureDatabase(IServiceCollection services) =>
        services.AddDbContext<AlmiranteDbContext>(options => { options.UseSqlServer(_connectionString); configureDb?.Invoke(options); });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
        command.ExecuteNonQuery();
    }
}

[Trait("Category", "RequiresSqlServer")]
public class SqlServerIntegrationTests
{
    [Fact]
    public async Task Startup_AplicaTodasAsMigrations_ESchemaFunciona()
    {
        using var factory = new SqlServerApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var (aplicadas, conhecidas) = await TestHelpers.WithDbAsync(factory, async db =>
            ((await db.Database.GetAppliedMigrationsAsync()).ToList(), db.Database.GetMigrations().ToList()));
        Assert.Equal(conhecidas, aplicadas);
        Assert.Contains("20260917120000_UnifyLancamentosFlow", aplicadas);

        // Consulta que usa as colunas renomeadas (Finalidade) e o JOIN com Usuarios.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Lancamentos?search=Mensalidade")).StatusCode);
    }

    [Fact]
    public async Task Lockout_TentativasConcorrentes_NaoPerdemIncrementos_ENaoUltrapassamOLimite()
    {
        using var factory = new SqlServerApiFactory(new Dictionary<string, string?>
        {
            ["LoginProtection:Lockout:MaxFailedAttempts"] = "5",
        });
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Concorrente", "DS");

        var respostas = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => TestHelpers.PostLoginAsync(client, membro.Email, "senha-errada-123")));
        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

        var estado = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking()
            .Where(u => u.Id == membro.Id).Select(u => new { u.FalhasLoginConsecutivas, u.LoginBloqueadoAteUtc }).SingleAsync());
        Assert.Equal(5, estado.FalhasLoginConsecutivas);
        Assert.NotNull(estado.LoginBloqueadoAteUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(client, membro.Email, TestHelpers.SenhaPadraoTeste)).StatusCode);
    }

    [Fact]
    public async Task Lockout_TentativasConcorrentesAbaixoDoLimite_ContamTodas()
    {
        using var factory = new SqlServerApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Concorrente", "DS");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => TestHelpers.PostLoginAsync(client, membro.Email, "senha-errada-123")));

        Assert.Equal(20, await TestHelpers.WithDbAsync(factory, db =>
            db.Usuarios.Where(u => u.Id == membro.Id).Select(u => u.FalhasLoginConsecutivas).SingleAsync()));
    }

    [Fact]
    public async Task ExclusaoLogica_RegistraResponsavelAutenticado_NaAuditoriaPorTrigger()
    {
        using var factory = new SqlServerApiFactory();
        var (client, tesoureiroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Auditado", "DS");

        var criado = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", new
        {
            membroId = membro.Id, finalidade = "Mensalidade", descricao = "auditoria", categoria = "Clube", tipoFluxo = "Entrada",
            valor = 15m, vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = false,
        });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var id = (await criado.Content.ReadFromJsonAsync<LancamentoDto>())!.Id;

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{id}") { Content = JsonContent.Create(new { motivo = "teste de auditoria" }) };
        delete.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, "198.51.100.23");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);

        var auditoria = await TestHelpers.WithDbAsync(factory, db => db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == id));
        Assert.Equal(tesoureiroId, auditoria.UsuarioResponsavelId);
        Assert.Equal("198.51.100.23", auditoria.IpResponsavel);
        Assert.Equal("teste de auditoria", auditoria.Motivo);
    }
}
