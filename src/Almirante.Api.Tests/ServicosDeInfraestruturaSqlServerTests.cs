using System.Data.Common;
using Almirante.Api.Data;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

/// <summary>Serviços em segundo plano e startup contra SQL Server real (os caminhos sem banco estão em <see cref="ServicosDeInfraestruturaTests"/>).</summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class ServicosDeInfraestruturaSqlServerTests
{
    [Fact]
    public async Task LimpezaDeSessoes_RemoveSoAsSessoesVencidasHaMaisDeUmaHora()
    {
        using var factory = new SqlServerApiFactory();
        factory.CreateClient();
        var admin = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.EmailNormalizado == "ADMIN@LOCAL.DEV").Select(u => u.Id).SingleAsync());
        var agora = DateTimeOffset.UtcNow;
        var (vencidaHaDuasHoras, vencidaNaMargem, vigente) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var conexao = new SqlConnection(factory.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = new SqlCommand("""
                INSERT INTO dbo.AuthSessions (Id, UsuarioId, CreatedAtUtc, LastRenewedAtUtc, AbsoluteExpiresAtUtc, SecurityVersion)
                VALUES (@a, @u, @criada, @criada, DATEADD(minute, -120, @agora), 0),
                       (@b, @u, @criada, @criada, DATEADD(minute, -30, @agora), 0),
                       (@c, @u, @criada, @criada, DATEADD(day, 1, @agora), 0);
                """, conexao);
            comando.Parameters.AddWithValue("@a", vencidaHaDuasHoras);
            comando.Parameters.AddWithValue("@b", vencidaNaMargem);
            comando.Parameters.AddWithValue("@c", vigente);
            comando.Parameters.AddWithValue("@u", admin);
            comando.Parameters.AddWithValue("@agora", agora.UtcDateTime);
            comando.Parameters.AddWithValue("@criada", agora.UtcDateTime.AddDays(-8));
            await comando.ExecuteNonQueryAsync();
        }

        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste(agora);
        using var servico = new AuthSessionCleanupService(factory.Services.GetRequiredService<IServiceScopeFactory>(), relogio,
            logs.CreateLogger<AuthSessionCleanupService>());
        await servico.StartAsync(CancellationToken.None);
        await relogio.AguardarTimerAsync();
        await servico.StopAsync(CancellationToken.None);

        var restantes = await TestHelpers.WithDbAsync(factory, db => db.AuthSessions.Select(s => s.Id).ToListAsync());
        Assert.DoesNotContain(vencidaHaDuasHoras, restantes);
        Assert.Contains(vencidaNaMargem, restantes);
        Assert.Contains(vigente, restantes);
        Assert.Contains(logs.Entries, e => e.StartsWith("Information Removidas 1 sessões de autenticação expiradas.", StringComparison.Ordinal));
    }

    private static async Task AguardarBloqueioAsync(string database, int sessaoQueBloqueia)
    {
        var fim = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < fim)
        {
            await using var harness = await OpenHarnessAsync(database);
            if (await ScalarAsync(harness, $"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {sessaoQueBloqueia}") > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("A rotação concorrente não chegou a esperar o lock do ALTER USER.");
    }

    [Fact]
    public async Task Rotacao_TrocaASenhaDeRuntimeACadaIntervalo_EParaSemErroDuranteUmaRotacaoTravada()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        var manager = env.NewManager();
        await manager.InitializeAsync();
        var senhaDoStartup = env.CurrentAppPassword();

        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        using var servico = new DbCredentialRotationService(manager,
            Microsoft.Extensions.Options.Options.Create(new DbCredentialOptions { AppUser = env.AppUser, RotationHours = 12 }), relogio,
            logs.CreateLogger<DbCredentialRotationService>());
        await servico.StartAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(12), await relogio.AguardarTimerAsync());
        relogio.Disparar();
        await relogio.AguardarTimerAsync();
        var senhaRotacionada = env.CurrentAppPassword();
        Assert.NotEqual(senhaDoStartup, senhaRotacionada);
        await using (var app = await env.OpenAppAsync())
        {
            Assert.Equal(1, await ScalarAsync(app, "SELECT 1"));
        }

        // Uma rotação manual fica presa no ALTER USER (outra sessão segura o lock do usuário numa transação aberta) e, com ela, o
        // semáforo do manager. A rotação periódica que dispara agora espera esse semáforo; parar o serviço nesse ponto não é falha.
        await using var bloqueio = await OpenHarnessAsync(env.Database);
        await ExecAsync(bloqueio, $"BEGIN TRANSACTION; ALTER USER [{env.AppUser}] WITH PASSWORD = '{NewPassword()}';");
        var spid = await ScalarAsync(bloqueio, "SELECT @@SPID");
        var rotacaoManual = Task.Run(() => manager.RotateAsync());
        await AguardarBloqueioAsync(env.Database, spid);

        relogio.Disparar();
        await servico.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(servico.ExecuteTask!.IsCompletedSuccessfully);

        await ExecAsync(bloqueio, "ROLLBACK TRANSACTION;");
        await rotacaoManual.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotEqual(senhaRotacionada, env.CurrentAppPassword());
        Assert.DoesNotContain(logs.Entries, e => e.StartsWith("Error", StringComparison.Ordinal));
    }

    /// <summary>No instante em que o EF vai criar o banco, outra "instância" o cria primeiro (erro 1801 para esta).</summary>
    private sealed class OutraInstanciaCriaOBanco(string harness, string database) : DbCommandInterceptor
    {
        public int Corridas { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Corridas == 0 && command.CommandText.Contains("CREATE DATABASE", StringComparison.OrdinalIgnoreCase))
            {
                Corridas++;
                await using var conexao = new SqlConnection(harness);
                await conexao.OpenAsync(cancellationToken);
                await using var criar = new SqlCommand($"CREATE DATABASE [{database}];", conexao);
                await criar.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task Migrate_QuandoOutraInstanciaCriaOBancoAoMesmoTempo_RepeteEMigraTudo()
    {
        var database = $"almirante_corrida_{Guid.NewGuid():N}";
        var harness = new SqlConnectionStringBuilder(HarnessConnectionString) { InitialCatalog = "master" }.ConnectionString;
        var alvo = new SqlConnectionStringBuilder(HarnessConnectionString) { InitialCatalog = database }.ConnectionString;
        var corrida = new OutraInstanciaCriaOBanco(harness, database);
        try
        {
            await using var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>()
                .UseSqlServer(alvo, sql => sql.CommandTimeout(300)).AddInterceptors(corrida).Options);

            await DbSeeder.MigrateWithRetryAsync(db, CancellationToken.None);

            Assert.Equal(1, corrida.Corridas);
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var conexao = new SqlConnection(harness);
            await conexao.OpenAsync();
            await using var remover = new SqlCommand(
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", conexao);
            await remover.ExecuteNonQueryAsync();
        }
    }
}
