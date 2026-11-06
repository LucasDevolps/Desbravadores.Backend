using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Tests;

/// <summary>
/// Issue #184 sem SQL Server: agenda da rotina de vencimento no fuso do clube, laço resiliente, texto da notificação e a regra do PUT
/// que recusa voltar a Pendente um lançamento vencido. A atualização em lote está em <see cref="LancamentosAtrasadosSqlServerTests"/>.
/// </summary>
public sealed class LancamentosAtrasadosTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    // Porta 1 em loopback recusa a conexão na hora: a falha é rápida e nunca toca um servidor de verdade.
    private const string SqlInalcancavel = "Server=127.0.0.1,1;Database=x;User Id=u;Password=p;Encrypt=False;Connect Timeout=2;ConnectRetryCount=0";

    private static DataCivil DataCivil(TimeProvider relogio) =>
        new(relogio, Microsoft.Extensions.Options.Options.Create(new ClubeOptions()));

    private static IServiceScopeFactory Escopos(Action<DbContextOptionsBuilder> configurar)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AlmiranteDbContext>(configurar);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static LancamentosAtrasadosService Servico(IServiceScopeFactory escopos, RelogioManualDeTeste relogio, ILoggerFactory logs) =>
        new(escopos, relogio, DataCivil(relogio), new SinalAvisosNotificacoes(), logs.CreateLogger<LancamentosAtrasadosService>());

    // --- Virada do dia no fuso do clube (America/Sao_Paulo, UTC-3) ---

    [Fact]
    public void Hoje_EAViradaDoDia_SeguemOFusoDoClube_NaoOUtc()
    {
        // 02:59:59 UTC de 07/11 ainda é 23:59:59 de 06/11 em São Paulo.
        var relogio = new RelogioManualDeTeste(new DateTimeOffset(2026, 11, 7, 2, 59, 59, TimeSpan.Zero));
        var dataCivil = DataCivil(relogio);

        Assert.Equal(new DateOnly(2026, 11, 6), dataCivil.Hoje());
        Assert.Equal(TimeSpan.FromSeconds(1), dataCivil.AteAViradaDoDia());

        relogio.Agora = new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 7), dataCivil.Hoje());
        Assert.Equal(TimeSpan.FromDays(1), dataCivil.AteAViradaDoDia());
    }

    [Theory]
    [InlineData("2026-11-06T12:00:00Z", 3600)] // 09:00 no clube: a próxima é em uma hora
    [InlineData("2026-11-07T00:00:00Z", 3600)] // meia-noite UTC é 21:00 no clube: nada de virada aqui
    [InlineData("2026-11-07T02:30:00Z", 1801)] // 23:30 no clube: logo após a meia-noite, com a folga de 1 s
    [InlineData("2026-11-07T02:00:00Z", 3600)] // 23:00 no clube: a hora cheia coincide com a virada (+1 s), fica a hora
    public void ProximaExecucao_ACadaHora_OuLogoAposAViradaDoDiaNoClube(string agora, int segundos)
    {
        var relogio = new RelogioManualDeTeste(DateTimeOffset.Parse(agora, System.Globalization.CultureInfo.InvariantCulture));
        using var servico = Servico(Escopos(o => o.UseInMemoryDatabase($"agenda-{Guid.NewGuid():N}")), relogio, new CapturingLoggerFactory());

        Assert.Equal(TimeSpan.FromSeconds(segundos), servico.ProximaExecucao());
    }

    // --- Laço em segundo plano ---

    [Fact]
    public async Task Rotina_SemBancoRelacional_NaoConsulta_EAgendaAProximaExecucao()
    {
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste(new DateTimeOffset(2026, 11, 7, 2, 30, 0, TimeSpan.Zero));
        using var servico = Servico(Escopos(o => o.UseInMemoryDatabase($"atrasados-{Guid.NewGuid():N}")), relogio, logs);

        await servico.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(1801), await relogio.AguardarTimerAsync());
        relogio.Agora = relogio.Agora.AddSeconds(1801);
        relogio.Disparar();
        Assert.Equal(TimeSpan.FromHours(1), await relogio.AguardarTimerAsync());
        Assert.Empty(logs.Entries);

        await servico.StopAsync(CancellationToken.None);
        Assert.True(servico.ExecuteTask!.IsCanceled);
    }

    [Fact]
    public async Task Rotina_FalhaDoBanco_SoRegistraErro_ETentaDeNovoNaProximaExecucao()
    {
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
        using var servico = Servico(Escopos(o => o.UseSqlServer(SqlInalcancavel)), relogio, logs);

        await servico.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(1), await relogio.AguardarTimerAsync());
        relogio.Disparar();
        Assert.Equal(TimeSpan.FromHours(1), await relogio.AguardarTimerAsync());

        Assert.Equal(2, logs.Entries.Count);
        Assert.All(logs.Entries, e => Assert.StartsWith("Error Falha ao marcar como Atrasado os lançamentos pendentes vencidos.", e));
        Assert.DoesNotContain(logs.Entries, e => e.Contains("Password=p", StringComparison.Ordinal));
        await servico.StopAsync(CancellationToken.None);
        Assert.True(servico.ExecuteTask!.IsCanceled);
    }

    [Fact]
    public async Task Rotina_ParadaNoMeioDaConsulta_EncerraSemRegistrarErro()
    {
        var logs = new CapturingLoggerFactory();
        var portao = new PortaoNaConexao();
        using var servico = Servico(Escopos(o => o.UseSqlServer(SqlInalcancavel).AddInterceptors(portao)), new RelogioManualDeTeste(), logs);

        await servico.StartAsync(CancellationToken.None);
        await portao.Chegou.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var parada = servico.StopAsync(CancellationToken.None);
        portao.Liberar.SetResult();
        await parada.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(servico.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Empty(logs.Entries);
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

    [Fact]
    public void Program_RegistraARotina_EAFabricaDeTestesSoAMantemQuandoPedido()
    {
        static bool Registrada(AlmiranteApiFactory f) => f.Services.GetServices<IHostedService>().OfType<LancamentosAtrasadosService>().Any();

        using var comRotina = new AlmiranteApiFactory { RotinaDeAtrasados = true };
        comRotina.CreateClient().Dispose();
        Assert.True(Registrada(comRotina));
        Assert.False(Registrada(factory));
    }

    // --- Notificação ---

    [Theory]
    [InlineData("Mensalidade", "Outubro", "Débito em atraso: Mensalidade", " referente a Mensalidade (Outubro) venceu")]
    [InlineData("Mensalidade", "  ", "Débito em atraso: Mensalidade", " referente a Mensalidade venceu")]
    [InlineData(null, "Evento - Parque - 01/11/2026", "Débito em atraso", " referente a Evento - Parque - 01/11/2026 venceu")]
    [InlineData(" ", null, "Débito em atraso", "R$ 1.234,50 venceu")]
    public void Notificacao_DebitoAtrasado_TextoChaveComVencimentoEAvisoPendente(string? finalidade, string? descricao, string titulo, string trecho)
    {
        var lancamento = new Lancamento
        {
            Id = Guid.NewGuid(), MembroId = Guid.NewGuid(), Finalidade = finalidade, Descricao = descricao, Categoria = CategoriaLancamento.Clube,
            Valor = 1234.5m, Vencimento = new DateOnly(2026, 11, 5), TipoFluxo = TipoFluxoLancamento.Entrada, EventoId = Guid.NewGuid(),
        };
        var agora = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        var n = NotificacoesFabrica.DebitoAtrasado(lancamento, agora);

        Assert.Equal((lancamento.MembroId!.Value, TipoNotificacao.DebitoAtrasado, $"atraso:{lancamento.Id:N}:20261105", lancamento.EventoId, (Guid?)lancamento.Id),
            (n.DestinatarioId, n.Tipo, n.ChaveOrigem, n.EventoId, n.LancamentoId));
        Assert.Equal(((AvisoNotificacao?)AvisoNotificacao.Criada, (DateTime?)agora, agora), (n.AvisoPendente, n.AvisoProximaTentativaEmUtc, n.CriadaEmUtc));
        Assert.Equal(titulo, n.Titulo);
        Assert.StartsWith("O débito de R$ 1.234,50", n.Mensagem);
        Assert.Contains(trecho, n.Mensagem);
        Assert.EndsWith("venceu em 05/11/2026 e ainda não tem pagamento registrado.", n.Mensagem);
    }

    // --- PUT /api/Lancamentos/{id}: Pendente com vencimento passado ---

    private async Task<(Guid Id, DateOnly Hoje)> InserirAsync(StatusLancamento status, int diasDoVencimento, Guid? eventoId = null)
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, $"Membro atraso {Guid.NewGuid():N}", "DS");
        var hoje = factory.Services.GetRequiredService<DataCivil>().Hoje();
        var lancamento = new Lancamento
        {
            Id = Guid.NewGuid(), MembroId = membro.Id, Finalidade = eventoId is null ? LancamentoFinalidades.Mensalidade : null,
            Descricao = "Outubro", Categoria = eventoId is null ? CategoriaLancamento.Clube : CategoriaLancamento.Evento, Valor = 30m,
            Vencimento = hoje.AddDays(diasDoVencimento), Status = status, TipoFluxo = TipoFluxoLancamento.Entrada, EventoId = eventoId,
        };
        await TestHelpers.WithDbAsync(factory, async db => { db.Lancamentos.Add(lancamento); return await db.SaveChangesAsync(); });
        return (lancamento.Id, hoje);
    }

    private Task<StatusLancamento> StatusAsync(Guid id) =>
        TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking().Where(l => l.Id == id).Select(l => l.Status).SingleAsync());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Put_AtrasadoParaPendente_ComVencimentoPassado_Retorna400_SemAlterar(bool deEvento)
    {
        var (id, hoje) = await InserirAsync(StatusLancamento.Atrasado, -1, deEvento ? Guid.NewGuid() : null);
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{id}", new { status = "Pendente" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problema = await response.Content.ReadAsStringAsync();
        Assert.Contains($"status Pendente exige vencimento a partir de {hoje:yyyy-MM-dd} (hoje no fuso do clube)", problema);
        Assert.Equal(StatusLancamento.Atrasado, await StatusAsync(id));
    }

    [Fact]
    public async Task Put_PagoHistoricoParaPendente_ComVencimentoPassado_Retorna400()
    {
        var (id, _) = await InserirAsync(StatusLancamento.Pago, -10);
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/Lancamentos/{id}", new { status = "Pendente" })).StatusCode);
        Assert.Equal(StatusLancamento.Pago, await StatusAsync(id));
    }

    [Theory]
    [InlineData(StatusLancamento.Atrasado, 0, null)]   // vence hoje: ainda não está atrasado
    [InlineData(StatusLancamento.Atrasado, 3, null)]   // vencimento futuro
    [InlineData(StatusLancamento.Atrasado, -3, 1)]    // prorrogado no mesmo pedido (amanhã: o validador já recusa data UTC passada)
    [InlineData(StatusLancamento.Pendente, -3, null)]  // reenviar o status atual não é troca (a rotina marca depois)
    public async Task Put_PendenteSemVencimentoPassado_OuSemTrocaDeStatus_EhAceito(StatusLancamento atual, int dias, int? novoVencimento)
    {
        var (id, hoje) = await InserirAsync(atual, dias);
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        object corpo = novoVencimento is { } d
            ? new { status = "Pendente", vencimento = hoje.AddDays(d).ToString("yyyy-MM-dd") }
            : new { status = "Pendente" };

        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{id}", corpo);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(StatusLancamento.Pendente, await StatusAsync(id));
    }
}
