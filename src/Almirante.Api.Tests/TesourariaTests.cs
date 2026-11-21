using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Services;

namespace Almirante.Api.Tests;

// Issues #190 e #199 sem SQL Server. Cada teste usa uma fábrica própria: os totais da Tesouraria são globais.
public sealed class TesourariaTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AlmiranteApiFactory _factory = new();
    private HttpClient _client = null!;
    private Guid _membroId;

    // Hoje no fuso do clube (Clube:FusoHorario padrão), a mesma referência da API.
    private static DateOnly Hoje => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);

    private static DateOnly MesAtual => new(Hoje.Year, Hoje.Month, 1);

    public async Task InitializeAsync()
    {
        _client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
        _membroId = (await TestHelpers.AddUsuarioAsync(_factory, "Membro da tesouraria", "CONS")).Id;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<TesourariaResumoResponse> ObterAsync()
    {
        var resposta = await _client.GetAsync("/api/Tesouraria/Resumo");
        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<TesourariaResumoResponse>(Json))!;
    }

    private Task<TesourariaResumoResponse> ObterNoInstanteAsync(DateTimeOffset agora)
    {
        var dataCivil = new DataCivil(new RelogioManualDeTeste(agora), Microsoft.Extensions.Options.Options.Create(new ClubeOptions()));
        return TestHelpers.WithDbAsync(_factory, db => new TesourariaService(db, dataCivil).ObterResumoAsync(CancellationToken.None));
    }

    private Task<Guid> LancamentoAsync(TipoFluxoLancamento fluxo, decimal valor, StatusLancamento status = StatusLancamento.Pendente,
        DateOnly? dataPagamento = null, decimal? valorPago = null, bool ativo = true) =>
        TestHelpers.WithDbAsync(_factory, async db =>
        {
            var lancamento = new Lancamento
            {
                Id = Guid.NewGuid(), MembroId = _membroId, Finalidade = LancamentoFinalidades.Outros, Categoria = CategoriaLancamento.Clube,
                TipoFluxo = fluxo, Valor = valor, Vencimento = dataPagamento ?? MesAtual, Status = status, Ativo = ativo,
                DataPagamento = dataPagamento, ValorPago = valorPago,
                PagamentoRegistradoPorUsuarioId = dataPagamento is null ? null : _membroId,
                PagamentoRegistradoEmUtc = dataPagamento is null ? null : DateTime.UtcNow,
            };
            db.Lancamentos.Add(lancamento);
            await db.SaveChangesAsync();
            return lancamento.Id;
        });

    private Task<Guid> PagoAsync(TipoFluxoLancamento fluxo, decimal valorPago, DateOnly data, decimal? valor = null) =>
        LancamentoAsync(fluxo, valor ?? valorPago, StatusLancamento.Pago, data, valorPago);

    [Fact(DisplayName = "Resumo sem movimentação devolve as seis competências até o mês atual, em ordem cronológica e zeradas")]
    public async Task TesourariaResumo_ObterResumo_SemMovimentoRetornaSeisCompetenciasZeradasAsync()
    {
        //Arrange
        var esperado = Enumerable.Range(-5, 6).Select(MesAtual.AddMonths)
            .Select(m => new TesourariaCompetenciaDto(m.Year, m.Month, 0m, 0m));

        //Act
        var resumo = await ObterAsync();

        //Assert
        Assert.Equal(esperado, resumo.ReceitasDespesas);
        Assert.Equal(0m, resumo.DespesasMes);
    }

    [Fact(DisplayName = "Resumo soma o valorPago por mês de dataPagamento e tipoFluxo, com zero nos meses sem movimento")]
    public async Task TesourariaResumo_ObterResumo_SomaValorPagoPorMesETipoFluxoAsync()
    {
        //Arrange
        var mesRetrasado = MesAtual.AddMonths(-2);
        await PagoAsync(TipoFluxoLancamento.Entrada, 100m, MesAtual.AddMonths(-5));
        await PagoAsync(TipoFluxoLancamento.Entrada, 33.33m, mesRetrasado);
        await PagoAsync(TipoFluxoLancamento.Entrada, 66.67m, mesRetrasado.AddMonths(1).AddDays(-1));
        await PagoAsync(TipoFluxoLancamento.Saida, 18.9m, mesRetrasado, valor: 20m);
        await PagoAsync(TipoFluxoLancamento.Saida, 40m, MesAtual);

        //Act
        var resumo = await ObterAsync();

        //Assert
        Assert.Equal(new[] { 100m, 0m, 0m, 100m, 0m, 0m }, resumo.ReceitasDespesas.Select(c => c.Entradas));
        Assert.Equal(new[] { 0m, 0m, 0m, 18.9m, 0m, 40m }, resumo.ReceitasDespesas.Select(c => c.Saidas));
    }

    [Fact(DisplayName = "Resumo ignora pendentes, atrasados, excluídos, \"Pago\" legados sem data ou valor e pagamentos fora das seis competências")]
    public async Task TesourariaResumo_ObterResumo_IgnoraLancamentosSemPagamentoRegistradoOuForaDaSerieAsync()
    {
        //Arrange
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 300m);
        await LancamentoAsync(TipoFluxoLancamento.Saida, 200m, StatusLancamento.Atrasado);
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 900m, StatusLancamento.Pago);
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 800m, StatusLancamento.Pago, dataPagamento: MesAtual);
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 700m, StatusLancamento.Pago, valorPago: 700m);
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 80m, StatusLancamento.Pago, MesAtual, 80m, ativo: false);
        await PagoAsync(TipoFluxoLancamento.Entrada, 500m, MesAtual.AddMonths(-5).AddDays(-1));
        await PagoAsync(TipoFluxoLancamento.Saida, 600m, MesAtual.AddMonths(1));
        await PagoAsync(TipoFluxoLancamento.Entrada, 5m, MesAtual);

        //Act
        var resumo = await ObterAsync();

        //Assert
        Assert.Equal(new[] { 0m, 0m, 0m, 0m, 0m, 5m }, resumo.ReceitasDespesas.Select(c => c.Entradas));
        Assert.All(resumo.ReceitasDespesas, c => Assert.Equal(0m, c.Saidas));
    }

    [Fact(DisplayName = "Lançamento excluído pela API sai do resumo da Tesouraria")]
    public async Task TesourariaResumo_ObterResumo_LancamentoExcluidoPelaApiNaoEntraAsync()
    {
        //Arrange
        var excluido = await PagoAsync(TipoFluxoLancamento.Saida, 30m, MesAtual);
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{excluido}") { Content = JsonContent.Create(new { motivo = "lançado em duplicidade" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);

        //Act
        var resumo = await ObterAsync();

        //Assert
        Assert.All(resumo.ReceitasDespesas, c => Assert.Equal(0m, c.Saidas));
    }

    [Fact(DisplayName = "Mês atual do resumo coincide com entradas e saídas do Dashboard sem parâmetros")]
    public async Task TesourariaResumo_ObterResumo_MesAtualCoincideComODashboardAsync()
    {
        //Arrange
        await PagoAsync(TipoFluxoLancamento.Entrada, 45m, MesAtual, valor: 50m);
        await PagoAsync(TipoFluxoLancamento.Entrada, 0.1m, Hoje);
        await PagoAsync(TipoFluxoLancamento.Saida, 18.9m, MesAtual.AddMonths(1).AddDays(-1));
        await PagoAsync(TipoFluxoLancamento.Entrada, 1000m, MesAtual.AddDays(-1));
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 900m, StatusLancamento.Pago);

        //Act
        var mesAtual = (await ObterAsync()).ReceitasDespesas[^1];
        var dashboard = await _client.GetFromJsonAsync<DashboardResponse>("/api/Dashboard", Json);

        //Assert
        Assert.Equal((MesAtual.Year, MesAtual.Month), (mesAtual.Ano, mesAtual.Mes));
        Assert.Equal((45.1m, 18.9m), (mesAtual.Entradas, mesAtual.Saidas));
        Assert.Equal((dashboard!.Financeiro!.Entradas, dashboard.Financeiro.Saidas), (mesAtual.Entradas, mesAtual.Saidas));
    }

    [Fact(DisplayName = "Na virada do ano a série atravessa dezembro e janeiro, mantendo a ordem cronológica e os anos corretos")]
    public async Task TesourariaResumo_ObterResumo_ViradaDoAnoMantemOrdemCronologicaAsync()
    {
        //Arrange
        await PagoAsync(TipoFluxoLancamento.Entrada, 500m, new DateOnly(2026, 8, 31));
        await PagoAsync(TipoFluxoLancamento.Entrada, 100m, new DateOnly(2026, 9, 1));
        await PagoAsync(TipoFluxoLancamento.Entrada, 250m, new DateOnly(2026, 12, 31));
        await PagoAsync(TipoFluxoLancamento.Saida, 40m, new DateOnly(2027, 1, 1));
        await PagoAsync(TipoFluxoLancamento.Saida, 7.5m, new DateOnly(2027, 2, 28));

        //Act
        var resumo = await ObterNoInstanteAsync(new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero));

        //Assert
        Assert.Equal(new TesourariaCompetenciaDto[]
        {
            new(2026, 9, 100m, 0m), new(2026, 10, 0m, 0m), new(2026, 11, 0m, 0m),
            new(2026, 12, 250m, 0m), new(2027, 1, 0m, 40m), new(2027, 2, 0m, 7.5m),
        }, resumo.ReceitasDespesas);
    }

    [Fact(DisplayName = "O mês atual da série segue o fuso do clube, não o UTC")]
    public async Task TesourariaResumo_ObterResumo_MesAtualSegueOFusoDoClubeAsync()
    {
        //Arrange
        // 02:30 UTC de 01/01/2027 ainda é 23:30 de 31/12/2026 em São Paulo.
        var aindaDezembroNoClube = new DateTimeOffset(2027, 1, 1, 2, 30, 0, TimeSpan.Zero);
        var janeiroNoClube = new DateTimeOffset(2027, 1, 1, 3, 0, 0, TimeSpan.Zero);

        //Act
        var emDezembro = await ObterNoInstanteAsync(aindaDezembroNoClube);
        var emJaneiro = await ObterNoInstanteAsync(janeiroNoClube);

        //Assert
        Assert.Equal(((2026, 7), (2026, 12)), ((emDezembro.ReceitasDespesas[0].Ano, emDezembro.ReceitasDespesas[0].Mes),
            (emDezembro.ReceitasDespesas[^1].Ano, emDezembro.ReceitasDespesas[^1].Mes)));
        Assert.Equal(((2026, 8), (2027, 1)), ((emJaneiro.ReceitasDespesas[0].Ano, emJaneiro.ReceitasDespesas[0].Mes),
            (emJaneiro.ReceitasDespesas[^1].Ano, emJaneiro.ReceitasDespesas[^1].Mes)));
    }

    [Fact(DisplayName = "despesasMes soma o valorPago das saídas pagas no mês atual, não o valor previsto, e coincide com as saídas do Dashboard")]
    public async Task TesourariaResumo_DespesasMes_SomaValorPagoDasSaidasDoMesAtualAsync()
    {
        //Arrange
        await PagoAsync(TipoFluxoLancamento.Saida, 18.9m, MesAtual, valor: 20m);
        await PagoAsync(TipoFluxoLancamento.Saida, 0.1m, Hoje);
        await PagoAsync(TipoFluxoLancamento.Saida, 31m, MesAtual.AddMonths(1).AddDays(-1));
        await PagoAsync(TipoFluxoLancamento.Entrada, 500m, MesAtual);

        //Act
        var resumo = await ObterAsync();
        var dashboard = await _client.GetFromJsonAsync<DashboardResponse>("/api/Dashboard", Json);

        //Assert
        Assert.Equal(50m, resumo.DespesasMes);
        Assert.Equal(resumo.ReceitasDespesas[^1].Saidas, resumo.DespesasMes);
        Assert.Equal(dashboard!.Financeiro!.Saidas, resumo.DespesasMes);
    }

    [Fact(DisplayName = "despesasMes ignora entradas, outros meses, excluídos, despesas em aberto e \"Pago\" legados sem data ou valor")]
    public async Task TesourariaResumo_DespesasMes_IgnoraLancamentosForaDaRegraAsync()
    {
        //Arrange
        await PagoAsync(TipoFluxoLancamento.Entrada, 70m, MesAtual);
        await PagoAsync(TipoFluxoLancamento.Saida, 60m, MesAtual.AddDays(-1));
        await PagoAsync(TipoFluxoLancamento.Saida, 55m, MesAtual.AddMonths(1));
        await LancamentoAsync(TipoFluxoLancamento.Saida, 50m, StatusLancamento.Pago, MesAtual, 50m, ativo: false);
        await LancamentoAsync(TipoFluxoLancamento.Saida, 40m);
        await LancamentoAsync(TipoFluxoLancamento.Saida, 30m, StatusLancamento.Atrasado);
        await LancamentoAsync(TipoFluxoLancamento.Saida, 20m, StatusLancamento.Pago);
        await LancamentoAsync(TipoFluxoLancamento.Saida, 15m, StatusLancamento.Pago, dataPagamento: MesAtual);
        await LancamentoAsync(TipoFluxoLancamento.Saida, 10m, StatusLancamento.Pago, valorPago: 10m);

        //Act
        var resumo = await ObterAsync();

        //Assert
        Assert.Equal(0m, resumo.DespesasMes);
    }

    [Fact(DisplayName = "Na virada do mês, despesasMes passa a contar só o mês novo, segundo o fuso do clube")]
    public async Task TesourariaResumo_DespesasMes_ViradaDoMesSegueOFusoDoClubeAsync()
    {
        //Arrange
        await PagoAsync(TipoFluxoLancamento.Saida, 12.5m, new DateOnly(2026, 12, 1));
        await PagoAsync(TipoFluxoLancamento.Saida, 7.5m, new DateOnly(2026, 12, 31));
        await PagoAsync(TipoFluxoLancamento.Saida, 3m, new DateOnly(2027, 1, 1));
        // 02:30 UTC de 01/01/2027 ainda é 23:30 de 31/12/2026 em São Paulo.
        var aindaDezembroNoClube = new DateTimeOffset(2027, 1, 1, 2, 30, 0, TimeSpan.Zero);
        var janeiroNoClube = new DateTimeOffset(2027, 1, 1, 3, 0, 0, TimeSpan.Zero);

        //Act
        var emDezembro = await ObterNoInstanteAsync(aindaDezembroNoClube);
        var emJaneiro = await ObterNoInstanteAsync(janeiroNoClube);

        //Assert
        Assert.Equal(20m, emDezembro.DespesasMes);
        Assert.Equal(3m, emJaneiro.DespesasMes);
    }

    [Fact(DisplayName = "Resumo exige financeiro.tesouraria.consultar: sem token 401, Secretário e cargos sem financeiro 403, ADM, DIR, DIRA e TES 200")]
    public async Task TesourariaResumo_ObterResumo_SoParaQuemTemGestaoFinanceiraAsync()
    {
        //Arrange
        await PagoAsync(TipoFluxoLancamento.Entrada, 80m, MesAtual);
        await PagoAsync(TipoFluxoLancamento.Saida, 25m, MesAtual);
        using var anonimo = _factory.CreateClient();
        var negados = new Dictionary<string, HttpResponseMessage>();
        var permitidos = new Dictionary<string, HttpStatusCode>();
        var despesasPorRole = new Dictionary<string, decimal?>();

        //Act
        var semToken = await anonimo.GetAsync("/api/Tesouraria/Resumo");
        foreach (var role in new[] { "SEC", "DS", "CONS", "TESU", "SECU", "CAP", "ALM" })
        {
            var (cliente, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, role);
            negados[role] = await cliente.GetAsync("/api/Tesouraria/Resumo");
        }

        foreach (var role in new[] { "ADM", "DIR", "DIRA", "TES" })
        {
            var (cliente, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, role);
            var resposta = await cliente.GetAsync("/api/Tesouraria/Resumo");
            permitidos[role] = resposta.StatusCode;
            despesasPorRole[role] = resposta.IsSuccessStatusCode
                ? (await resposta.Content.ReadFromJsonAsync<TesourariaResumoResponse>(Json))!.DespesasMes
                : null;
        }

        //Assert
        Assert.Equal(HttpStatusCode.Unauthorized, semToken.StatusCode);
        foreach (var (role, resposta) in negados)
        {
            Assert.True(resposta.StatusCode == HttpStatusCode.Forbidden, $"{role}: {(int)resposta.StatusCode}");
            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.DoesNotContain("receitasDespesas", corpo);
            Assert.DoesNotContain("despesasMes", corpo);
        }

        Assert.All(permitidos, p => Assert.Equal(HttpStatusCode.OK, p.Value));
        Assert.All(despesasPorRole, d => Assert.Equal(25m, d.Value));
    }
}
