using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Tests;

/// <summary>
/// Issue #184 contra SQL Server real: a rotina marca como Atrasado, em lote e em blocos, só os lançamentos ativos e Pendente vencidos no
/// fuso do clube, notifica os débitos uma vez, pula linhas travadas por outra transação e é segura com duas instâncias ao mesmo tempo.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentosAtrasadosSqlServerTests
{
    // 02:59:59 UTC de 07/11 ainda é 23:59:59 de 06/11 em São Paulo: a data UTC já virou, a do clube não.
    private static readonly DateTimeOffset AntesDaVirada = new(2026, 11, 7, 2, 59, 59, TimeSpan.Zero);
    private static readonly DateOnly Hoje = new(2026, 11, 6);

    /// <summary>Uma "instância" da rotina com relógio próprio, sobre o banco da factory (sem o laço do host, desligado nos testes).</summary>
    private sealed class Rotina : IDisposable
    {
        private readonly SqlServerApiFactory _factory;

        public Rotina(SqlServerApiFactory factory, DateTimeOffset agora)
        {
            _factory = factory;
            Relogio = new RelogioManualDeTeste(agora);
            Servico = new LancamentosAtrasadosService(factory.Services.GetRequiredService<IServiceScopeFactory>(), Relogio,
                new DataCivil(Relogio, Microsoft.Extensions.Options.Options.Create(new ClubeOptions())), Sinal,
                Logs.CreateLogger<LancamentosAtrasadosService>());
        }

        public RelogioManualDeTeste Relogio { get; }
        public CapturingLoggerFactory Logs { get; } = new();
        public SinalAvisosNotificacoes Sinal { get; } = new();
        public LancamentosAtrasadosService Servico { get; }

        public async Task<int> ExecutarAsync()
        {
            using var scope = _factory.Services.CreateScope();
            return await Servico.AtualizarAsync(scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>(), CancellationToken.None);
        }

        public void Dispose() => Servico.Dispose();
    }

    private static SqlServerApiFactory Fabrica(FalhasInjetaveisNoBanco? falhas = null)
    {
        var factory = new SqlServerApiFactory(configureDb: falhas is null ? null : o => o.AddInterceptors(falhas));
        factory.CreateClient().Dispose();
        return factory;
    }

    private static async Task<Lancamento> InserirAsync(SqlServerApiFactory factory, Guid? membroId, DateOnly vencimento,
        StatusLancamento status = StatusLancamento.Pendente, TipoFluxoLancamento fluxo = TipoFluxoLancamento.Entrada, bool ativo = true,
        Guid? eventoId = null)
    {
        var lancamento = new Lancamento
        {
            Id = Guid.NewGuid(), MembroId = membroId, Finalidade = eventoId is null ? LancamentoFinalidades.Mensalidade : null,
            Descricao = $"Atraso {Guid.NewGuid():N}", Categoria = eventoId is null ? CategoriaLancamento.Clube : CategoriaLancamento.Evento,
            Valor = 25m, Vencimento = vencimento, Status = status, TipoFluxo = fluxo, Ativo = ativo, EventoId = eventoId,
        };
        await TestHelpers.WithDbAsync(factory, async db => { db.Lancamentos.Add(lancamento); return await db.SaveChangesAsync(); });
        return lancamento;
    }

    private static Task<Lancamento> LerAsync(SqlServerApiFactory factory, Guid id) =>
        TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id));

    private static Task<List<Notificacao>> NotificacoesDeAtrasoAsync(SqlServerApiFactory factory) =>
        TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AsNoTracking().Where(n => n.Tipo == TipoNotificacao.DebitoAtrasado).ToListAsync());

    private static async Task<Guid> MembroAsync(SqlServerApiFactory factory) =>
        (await TestHelpers.AddUsuarioAsync(factory, $"Membro atraso {Guid.NewGuid():N}", "DS")).Id;

    [Fact]
    public async Task Rotina_NaSubidaENaViradaDoDia_MarcaSoPendentesAtivosVencidosNoFusoDoClube_ENotificaSoDebitos()
    {
        using var factory = Fabrica();
        var membro = await MembroAsync(factory);
        var vencido = await InserirAsync(factory, membro, Hoje.AddDays(-1));
        var venceHoje = await InserirAsync(factory, membro, Hoje);
        var futuro = await InserirAsync(factory, membro, Hoje.AddDays(4));
        var pago = await InserirAsync(factory, membro, Hoje.AddDays(-5), StatusLancamento.Pago);
        var inativo = await InserirAsync(factory, membro, Hoje.AddDays(-5), ativo: false);
        var saida = await InserirAsync(factory, membro, Hoje.AddDays(-5), fluxo: TipoFluxoLancamento.Saida);
        var semMembro = await InserirAsync(factory, null, Hoje.AddDays(-5));
        var jaAtrasado = await InserirAsync(factory, membro, Hoje.AddDays(-5), StatusLancamento.Atrasado);

        using var rotina = new Rotina(factory, AntesDaVirada);
        await rotina.Servico.StartAsync(CancellationToken.None);

        // Execução da subida: 1 s até a meia-noite do clube + 1 s de folga.
        Assert.Equal(TimeSpan.FromSeconds(2), await rotina.Relogio.AguardarTimerAsync());
        foreach (var marcado in new[] { vencido, saida, semMembro })
        {
            var lido = await LerAsync(factory, marcado.Id);
            Assert.Equal((StatusLancamento.Atrasado, (DateTime?)AntesDaVirada.UtcDateTime, (Guid?)null),
                (lido.Status, lido.DataAtualizacao, lido.AtualizadoPorUsuarioId));
        }

        foreach (var (intocado, status) in new[] { (venceHoje, StatusLancamento.Pendente), (futuro, StatusLancamento.Pendente),
                     (pago, StatusLancamento.Pago), (inativo, StatusLancamento.Pendente), (jaAtrasado, StatusLancamento.Atrasado) })
        {
            var lido = await LerAsync(factory, intocado.Id);
            Assert.Equal((status, (DateTime?)null), (lido.Status, lido.DataAtualizacao));
        }

        Assert.False((await LerAsync(factory, inativo.Id)).Ativo);
        var notificacao = Assert.Single(await NotificacoesDeAtrasoAsync(factory));
        Assert.Equal((membro, (Guid?)vencido.Id, $"atraso:{vencido.Id:N}:20261105", (AvisoNotificacao?)AvisoNotificacao.Criada),
            (notificacao.DestinatarioId, notificacao.LancamentoId, notificacao.ChaveOrigem, notificacao.AvisoPendente));
        await rotina.Sinal.AguardarAsync(TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("Information 3 lançamentos pendentes vencidos passaram a Atrasado.", Assert.Single(rotina.Logs.Entries));

        // Virada do dia no clube: o que vencia "hoje" passa a Atrasado.
        rotina.Relogio.Agora = AntesDaVirada.AddSeconds(2);
        rotina.Relogio.Disparar();
        Assert.Equal(TimeSpan.FromHours(1), await rotina.Relogio.AguardarTimerAsync());
        await rotina.Servico.StopAsync(CancellationToken.None);

        Assert.Equal(StatusLancamento.Atrasado, (await LerAsync(factory, venceHoje.Id)).Status);
        Assert.Equal(StatusLancamento.Pendente, (await LerAsync(factory, futuro.Id)).Status);
        Assert.Equal(2, (await NotificacoesDeAtrasoAsync(factory)).Count);
        Assert.StartsWith("Information 1 lançamentos pendentes vencidos passaram a Atrasado.", rotina.Logs.Entries[^1]);
    }

    [Fact]
    public async Task Rotina_SemVencidos_OuSoSemDebito_NaoNotificaNemSinaliza()
    {
        using var factory = Fabrica();
        using var rotina = new Rotina(factory, AntesDaVirada);
        Assert.Equal(0, await rotina.ExecutarAsync());

        await InserirAsync(factory, await MembroAsync(factory), Hoje.AddDays(-3), fluxo: TipoFluxoLancamento.Saida);
        Assert.Equal(1, await rotina.ExecutarAsync());

        Assert.Empty(await NotificacoesDeAtrasoAsync(factory));
        var espera = Stopwatch.StartNew();
        await rotina.Sinal.AguardarAsync(TimeSpan.FromMilliseconds(300), TimeProvider.System, CancellationToken.None);
        Assert.True(espera.Elapsed >= TimeSpan.FromMilliseconds(250), "Nenhum aviso foi gravado: o publicador não deveria ser acordado.");
        Assert.Empty(rotina.Logs.Entries);
    }

    [Fact]
    public async Task Atualizacao_EmBlocos_DuasInstanciasAoMesmoTempo_CadaLancamentoMudaENotificaUmaVez()
    {
        const int quantidade = 1201;
        using var factory = Fabrica();
        var membro = await MembroAsync(factory);
        await using (var conexao = new SqlConnection(factory.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = new SqlCommand("""
                INSERT INTO dbo.Lancamentos (Id, MembroId, Finalidade, Descricao, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, Ativo)
                SELECT TOP (@n) NEWID(), @membro, 'Mensalidade', 'Lote', 1, 10, DATEADD(day, -(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 30) - 1, @hoje),
                       0, 0, SYSUTCDATETIME(), 1
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                """, conexao);
            comando.Parameters.AddWithValue("@n", quantidade);
            comando.Parameters.AddWithValue("@membro", membro);
            comando.Parameters.Add("@hoje", System.Data.SqlDbType.Date).Value = Hoje.ToDateTime(TimeOnly.MinValue);
            Assert.Equal(quantidade, await comando.ExecuteNonQueryAsync());
        }

        using var a = new Rotina(factory, AntesDaVirada);
        using var b = new Rotina(factory, AntesDaVirada);
        var totais = await Task.WhenAll(Task.Run(a.ExecutarAsync), Task.Run(b.ExecutarAsync));

        Assert.Equal(quantidade, totais.Sum());
        Assert.Equal(quantidade, await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync(l => l.Status == StatusLancamento.Atrasado)));
        var notificacoes = await NotificacoesDeAtrasoAsync(factory);
        Assert.Equal(quantidade, notificacoes.Count);
        Assert.Equal(quantidade, notificacoes.Select(n => n.LancamentoId).Distinct().Count());

        // Idempotente: uma nova execução não acha mais nada.
        Assert.Equal(0, await a.ExecutarAsync());
        Assert.Equal(quantidade, (await NotificacoesDeAtrasoAsync(factory)).Count);
    }

    [Fact]
    public async Task LinhaTravadaPorOutraTransacao_EhPulada_EFicaParaAProximaExecucao()
    {
        using var factory = Fabrica();
        var membro = await MembroAsync(factory);
        var travado = await InserirAsync(factory, membro, Hoje.AddDays(-2));
        var livre = await InserirAsync(factory, membro, Hoje.AddDays(-2));
        using var rotina = new Rotina(factory, AntesDaVirada);

        // Um PUT ou pagamento em andamento segura a linha: a rotina não espera por ela nem a sobrescreve.
        await using (var conexao = new SqlConnection(factory.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var tx = (SqlTransaction)await conexao.BeginTransactionAsync();
            await using (var trava = new SqlCommand("SELECT Id FROM dbo.Lancamentos WITH (UPDLOCK, ROWLOCK) WHERE Id = @id", conexao, tx))
            {
                trava.Parameters.AddWithValue("@id", travado.Id);
                Assert.NotNull(await trava.ExecuteScalarAsync());
            }

            Assert.Equal(1, await rotina.ExecutarAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(StatusLancamento.Atrasado, (await LerAsync(factory, livre.Id)).Status);
            await tx.RollbackAsync();
        }

        Assert.Equal(StatusLancamento.Pendente, (await LerAsync(factory, travado.Id)).Status);
        Assert.Equal(1, await rotina.ExecutarAsync());
        Assert.Equal(StatusLancamento.Atrasado, (await LerAsync(factory, travado.Id)).Status);
        Assert.Equal(2, (await NotificacoesDeAtrasoAsync(factory)).Count);
    }

    [Fact]
    public async Task FalhaAoGravarANotificacao_DesfazAMudancaDeStatus_EAProximaExecucaoRefaz()
    {
        var falhas = new FalhasInjetaveisNoBanco();
        using var factory = Fabrica(falhas);
        var lancamento = await InserirAsync(factory, await MembroAsync(factory), Hoje.AddDays(-2));
        using var rotina = new Rotina(factory, AntesDaVirada);

        falhas.FalharProximoSave(() => new DbUpdateException("falha simulada ao gravar a notificação"));
        await Assert.ThrowsAsync<DbUpdateException>(rotina.ExecutarAsync);

        // Status e notificação vão juntos: o UPDATE do bloco foi desfeito com a transação.
        var lido = await LerAsync(factory, lancamento.Id);
        Assert.Equal((StatusLancamento.Pendente, (DateTime?)null), (lido.Status, lido.DataAtualizacao));
        Assert.Empty(await NotificacoesDeAtrasoAsync(factory));

        Assert.Equal(1, await rotina.ExecutarAsync());
        Assert.Equal(StatusLancamento.Atrasado, (await LerAsync(factory, lancamento.Id)).Status);
        Assert.Single(await NotificacoesDeAtrasoAsync(factory));
    }

    [Fact]
    public async Task AvisoJaCriadoParaOMesmoVencimento_NaoEhRepetido_EOsDemaisDoBlocoSaoNotificados()
    {
        using var factory = Fabrica();
        var membro = await MembroAsync(factory);
        var reincidente = await InserirAsync(factory, membro, Hoje.AddDays(-2));
        var novo = await InserirAsync(factory, membro, Hoje.AddDays(-2));
        var anterior = NotificacoesFabrica.DebitoAtrasado(reincidente, AntesDaVirada.UtcDateTime.AddDays(-1));
        anterior.AvisoPendente = null;
        anterior.AvisoProximaTentativaEmUtc = null;
        await TestHelpers.WithDbAsync(factory, async db => { db.Notificacoes.Add(anterior); return await db.SaveChangesAsync(); });

        using var rotina = new Rotina(factory, AntesDaVirada);
        Assert.Equal(2, await rotina.ExecutarAsync());

        var notificacoes = await NotificacoesDeAtrasoAsync(factory);
        Assert.Equal(2, notificacoes.Count);
        Assert.Equal(anterior.Id, notificacoes.Single(n => n.LancamentoId == reincidente.Id).Id);
        Assert.Single(notificacoes, n => n.LancamentoId == novo.Id);
    }

    [Fact]
    public async Task Listagem_MostraOVencidoComoAtrasado_EOMembroRecebeANotificacao()
    {
        using var factory = Fabrica();
        var (membroClient, membro) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var lancamento = await InserirAsync(factory, membro, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10));
        var eventoResposta = await EventosTestKit.PostAsync(admin, EventosTestKit.Corpo(new[] { membro }));
        var evento = await EventosTestKit.LerAsync(eventoResposta);
        await using (var conexao = new SqlConnection(factory.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = new SqlCommand("UPDATE dbo.Lancamentos SET Vencimento = DATEADD(day, -10, CAST(SYSUTCDATETIME() AS date)) WHERE EventoId = @e", conexao);
            comando.Parameters.AddWithValue("@e", evento.Id);
            Assert.Equal(1, await comando.ExecuteNonQueryAsync());
        }

        using var rotina = new Rotina(factory, DateTimeOffset.UtcNow);
        Assert.Equal(2, await rotina.ExecutarAsync());

        var semFiltro = await admin.GetFromJsonAsync<JsonObject>($"/api/Lancamentos?search={lancamento.Descricao}");
        Assert.Equal("Atrasado", (string?)semFiltro!["items"]![0]!["status"]);
        var atrasados = await admin.GetFromJsonAsync<JsonObject>("/api/Lancamentos?status=Atrasado&pageSize=100");
        Assert.Equal(2, atrasados!["items"]!.AsArray().Count);

        var pendentes = await membroClient.GetFromJsonAsync<JsonObject>("/api/Notificacoes/Pendentes?pageSize=20");
        var tipos = pendentes!["items"]!.AsArray().Where(n => (string?)n!["tipo"] == "DebitoAtrasado").ToList();
        Assert.Equal(2, tipos.Count);
        var doEvento = await TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AsNoTracking()
            .SingleAsync(n => n.Tipo == TipoNotificacao.DebitoAtrasado && n.EventoId == evento.Id));
        Assert.Equal(evento.Lancamentos[0].Id, doEvento.LancamentoId);
        Assert.Equal("Débito em atraso", doEvento.Titulo);
    }

    [Fact]
    public async Task Migration_AceitaODebitoAtrasado_EDownRemoveSoAsNotificacoesDoTipo4()
    {
        const string anterior = "20261026203804_AddAuditoriaHttp";
        using var factory = Fabrica();
        var membro = await MembroAsync(factory);
        var lancamento = await InserirAsync(factory, membro, Hoje.AddDays(-1));
        using (var rotina = new Rotina(factory, AntesDaVirada))
        {
            Assert.Equal(1, await rotina.ExecutarAsync());
        }

        await TestHelpers.WithDbAsync(factory, async db =>
        {
            db.Notificacoes.Add(NotificacoesFabrica.DebitoCadastrado(lancamento, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });

        await using (var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(factory.ConnectionString).Options))
        {
            await db.GetService<IMigrator>().MigrateAsync(anterior);
            Assert.Equal([TipoNotificacao.DebitoCadastrado], await db.Notificacoes.AsNoTracking().Select(n => n.Tipo).ToListAsync());
            Assert.Equal(StatusLancamento.Atrasado, await db.Lancamentos.Where(l => l.Id == lancamento.Id).Select(l => l.Status).SingleAsync());
            var recusa = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE dbo.notificacoes SET Tipo = 4"));
            Assert.Equal(547, recusa.Number);

            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("UPDATE dbo.notificacoes SET Tipo = 4"));
            var foraDoDominio = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.notificacoes SET Tipo = 5"));
            Assert.Equal(547, foraDoDominio.Number);
        }
    }
}
