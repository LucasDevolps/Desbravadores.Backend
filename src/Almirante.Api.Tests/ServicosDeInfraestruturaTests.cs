using System.Data.Common;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Hubs;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Tests;

/// <summary>
/// Serviços de infraestrutura sem SQL Server: fábrica de design-time, health check de conectividade, laços em segundo plano
/// (rotação da credencial de runtime e limpeza de sessões) e as validações do seed. O comportamento contra o banco real está em
/// <see cref="ServicosDeInfraestruturaSqlServerTests"/>.
/// </summary>
public sealed class ServicosDeInfraestruturaTests
{
    // Porta 1 em loopback recusa a conexão na hora: a falha é rápida e nunca toca um servidor de verdade.
    private const string SqlInalcancavel = "Server=127.0.0.1,1;Database=x;User Id=u;Password=p;Encrypt=False;Connect Timeout=2;ConnectRetryCount=0";

    private static IServiceScopeFactory Escopos(Action<DbContextOptionsBuilder> configurar)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AlmiranteDbContext>(configurar);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Segura a abertura da conexão até o teste liberar: dá tempo de pedir a parada no meio da consulta.</summary>
    private sealed class PortaoNaConexao : DbConnectionInterceptor
    {
        public TaskCompletionSource Chegou { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Chegou.TrySetResult();
            await Liberar.Task;
            return result;
        }
    }

    // --- AlmiranteDbContextFactory (somente design-time: dotnet ef) ---

    private static Microsoft.Data.SqlClient.SqlConnectionStringBuilder ConnectionStringDeDesignTime(string? variavel)
    {
        var original = Environment.GetEnvironmentVariable("ALMIRANTE_DESIGN_TIME_CONNECTION");
        Environment.SetEnvironmentVariable("ALMIRANTE_DESIGN_TIME_CONNECTION", variavel);
        try
        {
            using var db = new AlmiranteDbContextFactory().CreateDbContext([]);
            return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.Database.GetConnectionString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALMIRANTE_DESIGN_TIME_CONNECTION", original);
        }
    }

    [Fact]
    public void FabricaDeDesignTime_SemVariavel_UsaOSqlServerLocalComAutenticacaoDoWindows()
    {
        var conexao = ConnectionStringDeDesignTime(null);
        Assert.Equal(("localhost", "almirante", true, true, ""), (conexao.DataSource, conexao.InitialCatalog, conexao.IntegratedSecurity,
            conexao.TrustServerCertificate, conexao.UserID));
    }

    [Fact]
    public void FabricaDeDesignTime_UsaAConexaoDaVariavel()
    {
        var conexao = ConnectionStringDeDesignTime("Server=127.0.0.1,14331;Database=almirante_ef;User Id=almirante_admin_bd;Password=placeholder;Encrypt=True");
        Assert.Equal(("127.0.0.1,14331", "almirante_ef", "almirante_admin_bd", false), (conexao.DataSource, conexao.InitialCatalog, conexao.UserID,
            conexao.IntegratedSecurity));
    }

    [Theory]
    [InlineData("Server=x;Database=d;User Id=sa;Password=p")]
    [InlineData("Server=x;Database=d;User Id=[SA];Password=p")]
    public void FabricaDeDesignTime_RecusaSa(string conexao)
    {
        var erro = Assert.Throws<InvalidOperationException>(() => ConnectionStringDeDesignTime(conexao));
        Assert.Equal("ALMIRANTE_DESIGN_TIME_CONNECTION não pode usar o login 'sa'.", erro.Message);
    }

    // --- DbConnectivityHealthCheck ---

    [Fact]
    public async Task HealthCheckDoBanco_ComConexao_EhSaudavel()
    {
        var check = new DbConnectivityHealthCheck(Escopos(o => o.UseInMemoryDatabase($"hc-{Guid.NewGuid():N}")));
        var resultado = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, resultado.Status);
    }

    [Fact]
    public async Task HealthCheckDoBanco_SemConexao_EhInsalubreComMensagemSemDetalhesDaConexao()
    {
        var check = new DbConnectivityHealthCheck(Escopos(o => o.UseSqlServer(SqlInalcancavel)));
        var resultado = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, resultado.Status);
        Assert.Equal("Sem conexão com o banco de dados.", resultado.Description);
        Assert.Null(resultado.Exception);
    }

    // --- DbCredentialRotationService ---

    private static DbCredentialManager ManagerInalcancavel(CapturingLoggerFactory logs) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = "Server=127.0.0.1,1;Database=x;Encrypt=False",
            [$"ConnectionStrings:{DbCredentialManager.AdminConnectionName}"] = SqlInalcancavel,
        }).Build(),
        Microsoft.Extensions.Options.Options.Create(new DbCredentialOptions { AppUser = "almirante_user_bd" }),
        new AppDbCredentialProvider(),
        logs.CreateLogger<DbCredentialManager>());

    [Theory]
    [InlineData(24, 24)]
    [InlineData(0, 1)]
    public async Task Rotacao_EsperaOIntervalo_RegistraFalhaSemDerrubarOLaco_EParaQuandoPedido(int horasConfiguradas, int horasEsperadas)
    {
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        using var servico = new DbCredentialRotationService(ManagerInalcancavel(logs),
            Microsoft.Extensions.Options.Options.Create(new DbCredentialOptions { AppUser = "almirante_user_bd", RotationHours = horasConfiguradas }),
            relogio, logs.CreateLogger<DbCredentialRotationService>());

        await servico.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(horasEsperadas), await relogio.AguardarTimerAsync());
        Assert.Empty(logs.Entries);

        relogio.Disparar();
        Assert.Equal(TimeSpan.FromHours(horasEsperadas), await relogio.AguardarTimerAsync());
        var erro = Assert.Single(logs.Entries);
        Assert.StartsWith("Error Falha ao rotacionar a senha do usuário SQL da aplicação.", erro);
        Assert.Contains("Falha ao rotacionar a senha da identidade de runtime (erro SQL", erro);
        Assert.DoesNotContain("Password=p", erro);

        await servico.StopAsync(CancellationToken.None);
        Assert.True(servico.ExecuteTask!.IsCompletedSuccessfully);
    }

    // --- MonitorSessoesNotificacoes ---

    [Fact]
    public async Task MonitorDoHub_BancoFora_RegistraAFalhaSemDerrubarOLaco_NemEncerrarAConexao()
    {
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        var conexoes = new ConexoesNotificacoes();
        var encerrada = false;
        conexoes.Registrar(new ConexaoNotificacoes("c1", Guid.NewGuid(), Guid.NewGuid(), "DS", () => encerrada = true));
        using var monitor = new Almirante.Api.Services.MonitorSessoesNotificacoes(conexoes, Escopos(o => o.UseSqlServer(SqlInalcancavel)), relogio,
            Microsoft.Extensions.Options.Options.Create(new NotificacoesOptions()), logs.CreateLogger<Almirante.Api.Services.MonitorSessoesNotificacoes>());

        await monitor.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromMilliseconds(new NotificacoesOptions().VerificacaoSessoesMs), await relogio.AguardarTimerAsync());
        relogio.Disparar();
        await relogio.AguardarTimerAsync();

        Assert.StartsWith("Error Falha ao revalidar as sessões das conexões do hub de notificações.", Assert.Single(logs.Entries));
        Assert.False(encerrada);
        Assert.Single(conexoes.Abertas());
        await monitor.StopAsync(CancellationToken.None);
        Assert.True(monitor.ExecuteTask!.IsCompletedSuccessfully);
    }

    // --- AuthSessionCleanupService ---

    [Fact]
    public async Task LimpezaDeSessoes_SemBancoRelacional_NaoConsulta_EAgendaAProximaEm6Horas()
    {
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        using var servico = new AuthSessionCleanupService(Escopos(o => o.UseInMemoryDatabase($"limpeza-{Guid.NewGuid():N}")), relogio,
            logs.CreateLogger<AuthSessionCleanupService>());

        await servico.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(6), await relogio.AguardarTimerAsync());
        relogio.Disparar();
        Assert.Equal(TimeSpan.FromHours(6), await relogio.AguardarTimerAsync());
        Assert.Empty(logs.Entries);

        await servico.StopAsync(CancellationToken.None);
        Assert.True(servico.ExecuteTask!.IsCanceled);
    }

    [Fact]
    public async Task LimpezaDeSessoes_FalhaDoBanco_EhRegistrada_ELacoContinua()
    {
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        using var servico = new AuthSessionCleanupService(Escopos(o => o.UseSqlServer(SqlInalcancavel)), relogio,
            logs.CreateLogger<AuthSessionCleanupService>());

        await servico.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(6), await relogio.AguardarTimerAsync());

        var erro = Assert.Single(logs.Entries);
        Assert.StartsWith("Error Falha na limpeza de sessões expiradas.", erro);
        Assert.DoesNotContain("Password=p", erro);
        await servico.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LimpezaDeSessoes_ParadaNoMeioDaConsulta_EncerraSemRegistrarErro()
    {
        var logs = new CapturingLoggerFactory();
        var portao = new PortaoNaConexao();
        using var servico = new AuthSessionCleanupService(Escopos(o => o.UseSqlServer(SqlInalcancavel).AddInterceptors(portao)),
            new RelogioManualDeTeste(), logs.CreateLogger<AuthSessionCleanupService>());

        await servico.StartAsync(CancellationToken.None);
        await portao.Chegou.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var parada = servico.StopAsync(CancellationToken.None);
        portao.Liberar.SetResult();
        await parada.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(servico.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Empty(logs.Entries);
    }

    // --- DbSeeder: validações do administrador inicial e lançamento demonstrativo ---

    private static async Task<AlmiranteDbContext> BancoEmMemoriaAsync()
    {
        var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>().UseInMemoryDatabase($"seed-{Guid.NewGuid():N}").Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    [Theory]
    [InlineData("", "SeedAdmin:Email é obrigatório.")]
    [InlineData("   ", "SeedAdmin:Email é obrigatório.")]
    [InlineData("joão@local.dev", "SeedAdmin:Email deve ter no máximo 100 caracteres ASCII (coluna Usuarios.Email varchar).")]
    public async Task Seed_RecusaEmailDoAdministradorInvalido_SemCriarNenhumUsuario(string email, string mensagem)
    {
        await using var db = await BancoEmMemoriaAsync();
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => DbSeeder.SeedAsync(db, new PasswordHasher<Usuario>(),
            Microsoft.Extensions.Options.Options.Create(new SeedOptions { Nome = "Admin", Email = email, Senha = "Uma frase longa do administrador 2026" })));
        Assert.Equal(mensagem, erro.Message);
        Assert.False(await db.Usuarios.AnyAsync());
    }

    [Fact]
    public async Task Seed_RecusaEmailDoAdministradorAcimaDe100Caracteres()
    {
        await using var db = await BancoEmMemoriaAsync();
        var email = new string('a', 90) + "@local.dev.br";
        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => DbSeeder.SeedAsync(db, new PasswordHasher<Usuario>(),
            Microsoft.Extensions.Options.Options.Create(new SeedOptions { Nome = "Admin", Email = email, Senha = "Uma frase longa do administrador 2026" })));
        Assert.Contains("no máximo 100 caracteres", erro.Message);
    }

    [Fact]
    public async Task Seed_SemNenhumUsuarioAtivo_NaoCriaLancamentoDemonstrativo()
    {
        await using var db = await BancoEmMemoriaAsync();
        var seed = Microsoft.Extensions.Options.Options.Create(new SeedOptions
        {
            Nome = "Admin", Email = "admin.seed@local.dev", Senha = "Uma frase longa do administrador 2026",
        });
        await DbSeeder.SeedAsync(db, new PasswordHasher<Usuario>(), seed);
        db.Lancamentos.RemoveRange(await db.Lancamentos.ToListAsync());
        var admin = await db.Usuarios.SingleAsync();
        admin.Ativo = false;
        await db.SaveChangesAsync();

        await DbSeeder.SeedAsync(db, new PasswordHasher<Usuario>(), seed);

        Assert.False(await db.Lancamentos.AnyAsync());
        Assert.Equal(1, await db.Usuarios.CountAsync());
    }
}
