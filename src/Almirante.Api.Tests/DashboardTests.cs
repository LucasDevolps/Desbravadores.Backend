using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Cada teste usa uma fábrica própria: os indicadores do dashboard são globais.
public sealed class DashboardTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AlmiranteApiFactory _factory = new();
    private HttpClient _client = null!;
    private Guid _membroId;

    // Hoje no fuso do clube (Clube:FusoHorario padrão), a mesma referência da API.
    private static DateOnly Hoje => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);

    private static readonly DateOnly Base = new(2025, 3, 1);

    public async Task InitializeAsync()
    {
        _client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
        _membroId = (await TestHelpers.AddUsuarioAsync(_factory, "Membro financeiro", "CONS")).Id;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<DashboardResponse> ObterAsync(string? query = null)
    {
        var resposta = await _client.GetAsync("/api/Dashboard" + query);
        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<DashboardResponse>(Json))!;
    }

    private static string Periodo(DateOnly inicial, DateOnly final) => $"?dataInicial={inicial:yyyy-MM-dd}&dataFinal={final:yyyy-MM-dd}";

    private Task<Guid> LancamentoAsync(TipoFluxoLancamento fluxo, decimal valor, StatusLancamento status = StatusLancamento.Pendente,
        DateOnly? dataPagamento = null, decimal? valorPago = null, bool ativo = true) =>
        TestHelpers.WithDbAsync(_factory, async db =>
        {
            var lancamento = new Lancamento
            {
                Id = Guid.NewGuid(), MembroId = _membroId, Finalidade = LancamentoFinalidades.Outros, Categoria = CategoriaLancamento.Clube,
                TipoFluxo = fluxo, Valor = valor, Vencimento = Base, Status = status, Ativo = ativo,
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

    private Task<Guid> EventoAsync(DateOnly data, string local, int participantes, Guid? grupoId = null, bool ativo = true,
        int participantesInativos = 0, string? titulo = null) =>
        TestHelpers.WithDbAsync(_factory, async db =>
        {
            var id = Guid.NewGuid();
            var grupo = grupoId ?? id;
            var evento = new Evento
            {
                Id = id, EventoGrupoId = grupo, EventoReferenciaId = grupoId, DataEvento = data, Titulo = titulo ?? local, Local = local, Ativo = ativo,
                CriadoPorUsuarioId = _membroId, CriadoEmUtc = DateTime.UtcNow,
            };
            for (var i = 0; i < participantes + participantesInativos; i++)
            {
                evento.Membros.Add(new EventoMembro
                {
                    Id = Guid.NewGuid(), EventoId = id, EventoGrupoId = grupo, MembroId = Guid.NewGuid(), LancamentoId = Guid.NewGuid(),
                    Ativo = i < participantes, CriadoEmUtc = DateTime.UtcNow,
                });
            }

            db.Eventos.Add(evento);
            await db.SaveChangesAsync();
            return id;
        });

    [Fact]
    public async Task SemParametros_UsaOMesAtualNoFusoDoClube_ESemMovimentacaoRetornaZeros()
    {
        var dashboard = await ObterAsync();

        var inicio = new DateOnly(Hoje.Year, Hoje.Month, 1);
        Assert.Equal(new DashboardPeriodoDto(inicio, inicio.AddMonths(1).AddDays(-1)), dashboard.Periodo);
        Assert.Equal(new DashboardFinanceiroDto(0m, 0m, 0m), dashboard.Financeiro);
        Assert.Equal(0, dashboard.Desbravadores.QuantidadeAtivos);
        Assert.Empty(dashboard.ProximosEventos);
        Assert.Equal(new DashboardAtividadesPlanejadasDto(Hoje, Hoje.AddDays(29), 0), dashboard.AtividadesPlanejadas);
        Assert.Empty(dashboard.AtividadesRecentes);
    }

    [Fact]
    public async Task MesAtualPadrao_SomaPagamentosDoMes_EIgnoraOsDoMesAnterior()
    {
        var inicioDoMes = new DateOnly(Hoje.Year, Hoje.Month, 1);
        await PagoAsync(TipoFluxoLancamento.Entrada, 70m, inicioDoMes);
        await PagoAsync(TipoFluxoLancamento.Entrada, 500m, inicioDoMes.AddDays(-1));

        Assert.Equal(new DashboardFinanceiroDto(70m, 70m, 0m), (await ObterAsync()).Financeiro);
    }

    [Fact]
    public async Task SomenteEntradasRealizadas()
    {
        await PagoAsync(TipoFluxoLancamento.Entrada, 100m, Base);
        await PagoAsync(TipoFluxoLancamento.Entrada, 25.5m, Base.AddDays(3));

        Assert.Equal(new DashboardFinanceiroDto(125.5m, 125.5m, 0m), (await ObterAsync(Periodo(Base, Base.AddDays(30)))).Financeiro);
    }

    [Fact]
    public async Task SomenteSaidasRealizadas_SaldoNegativo()
    {
        await PagoAsync(TipoFluxoLancamento.Saida, 40m, Base.AddDays(1));

        Assert.Equal(new DashboardFinanceiroDto(-40m, 0m, 40m), (await ObterAsync(Periodo(Base, Base.AddDays(30)))).Financeiro);
    }

    [Fact]
    public async Task EntradasESaidasNoMesmoPeriodo_SaldoEhAdiferenca()
    {
        await PagoAsync(TipoFluxoLancamento.Entrada, 2500m, Base.AddDays(2));
        await PagoAsync(TipoFluxoLancamento.Saida, 1000m, Base.AddDays(5));

        Assert.Equal(new DashboardFinanceiroDto(1500m, 2500m, 1000m), (await ObterAsync(Periodo(Base, Base.AddDays(30)))).Financeiro);
    }

    [Fact]
    public async Task PendentesAtrasadosEPagoHistoricoSemDados_NaoEntram()
    {
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 300m);
        await LancamentoAsync(TipoFluxoLancamento.Saida, 200m, StatusLancamento.Atrasado);
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 900m, StatusLancamento.Pago);
        await PagoAsync(TipoFluxoLancamento.Entrada, 10m, Base);

        Assert.Equal(new DashboardFinanceiroDto(10m, 10m, 0m), (await ObterAsync(Periodo(Base, Base.AddDays(30)))).Financeiro);
    }

    [Fact]
    public async Task ValorPagoDiferenteDoPrevisto_UsaOValorEfetivamenteLiquidado()
    {
        await PagoAsync(TipoFluxoLancamento.Entrada, 45m, Base, valor: 50m);
        await PagoAsync(TipoFluxoLancamento.Saida, 18.9m, Base, valor: 20m);

        Assert.Equal(new DashboardFinanceiroDto(26.1m, 45m, 18.9m), (await ObterAsync(Periodo(Base, Base))).Financeiro);
    }

    [Fact]
    public async Task LimitesInclusivos_EMovimentacoesForaDoPeriodo()
    {
        var inicial = Base.AddDays(10);
        var final = Base.AddDays(20);
        await PagoAsync(TipoFluxoLancamento.Entrada, 1m, inicial);
        await PagoAsync(TipoFluxoLancamento.Entrada, 2m, final);
        await PagoAsync(TipoFluxoLancamento.Entrada, 100m, inicial.AddDays(-1));
        await PagoAsync(TipoFluxoLancamento.Saida, 200m, final.AddDays(1));

        Assert.Equal(new DashboardFinanceiroDto(3m, 3m, 0m), (await ObterAsync(Periodo(inicial, final))).Financeiro);
        Assert.Equal(new DashboardFinanceiroDto(2m, 2m, 0m), (await ObterAsync(Periodo(final, final))).Financeiro);
    }

    [Fact]
    public async Task LancamentosExcluidosLogicamente_NaoEntram()
    {
        await LancamentoAsync(TipoFluxoLancamento.Entrada, 80m, StatusLancamento.Pago, Base, 80m, ativo: false);
        var excluido = await PagoAsync(TipoFluxoLancamento.Saida, 30m, Base);
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{excluido}") { Content = JsonContent.Create(new { motivo = "lançado em duplicidade" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);
        await PagoAsync(TipoFluxoLancamento.Entrada, 5m, Base);

        Assert.Equal(new DashboardFinanceiroDto(5m, 5m, 0m), (await ObterAsync(Periodo(Base, Base))).Financeiro);
    }

    [Fact]
    public async Task PrecisaoMonetaria_SomaCentavosSemArredondamento()
    {
        for (var i = 0; i < 3; i++) await PagoAsync(TipoFluxoLancamento.Entrada, 33.33m, Base);
        await PagoAsync(TipoFluxoLancamento.Entrada, 0.1m, Base);
        await PagoAsync(TipoFluxoLancamento.Entrada, 0.2m, Base);
        await PagoAsync(TipoFluxoLancamento.Saida, 0.3m, Base);

        var resposta = await _client.GetAsync("/api/Dashboard" + Periodo(Base, Base));
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var financeiro = json.RootElement.GetProperty("financeiro");
        Assert.Equal((100.29m, 0.3m, 99.99m),
            (financeiro.GetProperty("entradas").GetDecimal(), financeiro.GetProperty("saidas").GetDecimal(), financeiro.GetProperty("saldo").GetDecimal()));
    }

    [Fact]
    public async Task PagamentoRegistradoPelaApi_EntraNoPeriodoDaDataDePagamento_ENaoDoVencimento()
    {
        var membro = await TestHelpers.AddUsuarioAsync(_factory, "Pagante", "DS");
        var criado = await _client.PostAsJsonAsync("/api/Lancamentos/Registrar", new
        {
            membroId = membro.Id, finalidade = "Mensalidade", categoria = "Clube", tipoFluxo = "Entrada", valor = 60m,
            vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(15).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = false,
        });
        var id = (await criado.Content.ReadFromJsonAsync<LancamentoDto>(Json))!.Id;
        Assert.Equal(new DashboardFinanceiroDto(0m, 0m, 0m), (await ObterAsync(Periodo(Base, Base.AddDays(30)))).Financeiro);

        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, id, 55m, Base.AddDays(4))).StatusCode);

        Assert.Equal(new DashboardFinanceiroDto(55m, 55m, 0m), (await ObterAsync(Periodo(Base, Base.AddDays(30)))).Financeiro);
    }

    [Fact]
    public async Task Desbravadores_ContaSomenteCargoDsAtivo_IgnorandoDiretoriaOutrosCargosEInativos()
    {
        await TestHelpers.AddUsuarioAsync(_factory, "Desbravador 1", "DS");
        await TestHelpers.AddUsuarioAsync(_factory, "Desbravador 2", "DS");
        var inativo = await TestHelpers.AddUsuarioAsync(_factory, "Desbravador inativo", "DS");
        foreach (var role in new[] { "ADM", "DIR", "DIRA", "SEC", "TES", "CONS", "CPT", "INST", "CAP" })
            await TestHelpers.AddUsuarioAsync(_factory, $"Cargo {role}", role);
        await TestHelpers.WithDbAsync(_factory, async db =>
        {
            (await db.Usuarios.SingleAsync(u => u.Id == inativo.Id)).Ativo = false;
            return await db.SaveChangesAsync();
        });

        var dashboard = await ObterAsync(Periodo(Base, Base));

        Assert.Equal(2, dashboard.Desbravadores.QuantidadeAtivos);
    }

    [Fact]
    public async Task Desbravadores_NaoDependemDoPeriodoFinanceiro()
    {
        await TestHelpers.AddUsuarioAsync(_factory, "Desbravador", "DS");

        Assert.Equal(1, (await ObterAsync(Periodo(new DateOnly(2001, 1, 1), new DateOnly(2001, 1, 2)))).Desbravadores.QuantidadeAtivos);
    }

    [Fact]
    public async Task ProximosEventos_IncluiHojeEFuturos_ExcluiPassadosEExcluidos_EIgnoraOFiltroFinanceiro()
    {
        var hoje = await EventoAsync(Hoje, "Clube", 3, participantesInativos: 2, titulo: "Reunião");
        var futuro = await EventoAsync(Hoje.AddDays(30), "Acampamento", 1);
        await EventoAsync(Hoje.AddDays(-1), "Passado", 4);
        await EventoAsync(Hoje.AddDays(5), "Excluído", 4, ativo: false);

        var eventos = (await ObterAsync(Periodo(Base, Base))).ProximosEventos;

        Assert.Equal(new[] { hoje, futuro }, eventos.Select(e => e.EventoGrupoId));
        Assert.Equal((Hoje, "Reunião", "Clube", 3), (eventos[0].Data, eventos[0].Titulo, eventos[0].Local, eventos[0].QuantidadeParticipantes));
        Assert.Equal((Hoje.AddDays(30), "Acampamento", 1), (eventos[1].Data, eventos[1].Local, eventos[1].QuantidadeParticipantes));
    }

    [Fact]
    public async Task ProximosEventos_GruposDoMesmoPasseio_AparecemUmaVez_SomandoParticipantesDosCadastrosAtivos()
    {
        var data = Hoje.AddDays(12);
        var raiz = await EventoAsync(data, "Campori", 4);
        var segundo = await EventoAsync(data, "Campori", 2, grupoId: raiz);
        await EventoAsync(data, "Campori", 9, grupoId: raiz, ativo: false);

        var evento = Assert.Single((await ObterAsync()).ProximosEventos);

        Assert.Equal((raiz, data, "Campori", 6), (evento.EventoGrupoId, evento.Data, evento.Local, evento.QuantidadeParticipantes));
        Assert.Equal(new[] { raiz, segundo }.Order(), evento.EventoIds);
    }

    [Fact]
    public async Task ProximosEventos_CadastroRaizExcluido_PasseioContinuaPelosOutrosGrupos()
    {
        var data = Hoje.AddDays(3);
        var raiz = await EventoAsync(data, "Trilha", 2, ativo: false);
        var restante = await EventoAsync(data, "Trilha", 5, grupoId: raiz);

        var evento = Assert.Single((await ObterAsync()).ProximosEventos);

        Assert.Equal((raiz, 5), (evento.EventoGrupoId, evento.QuantidadeParticipantes));
        Assert.Equal(new[] { restante }, evento.EventoIds);
    }

    [Fact]
    public async Task ProximosEventos_OrdenadosPorData_ELimitadosACinco()
    {
        for (var i = 7; i >= 1; i--) await EventoAsync(Hoje.AddDays(i), $"Local {i}", i);

        var eventos = (await ObterAsync()).ProximosEventos;

        Assert.Equal(Almirante.Api.Services.DashboardService.LimiteProximosEventos, eventos.Count);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => Hoje.AddDays(i)), eventos.Select(e => e.Data));
    }

    [Fact]
    public async Task AtividadesPlanejadas_ContaDeHojeAteHojeMais29_ExcluiForaDaJanelaEExcluidos_EIgnoraOFiltroFinanceiro()
    {
        await EventoAsync(Hoje, "Hoje", 1);
        await EventoAsync(Hoje.AddDays(29), "Último dia", 1);
        await EventoAsync(Hoje.AddDays(-1), "Ontem", 1);
        await EventoAsync(Hoje.AddDays(30), "Depois da janela", 1);
        await EventoAsync(Hoje.AddDays(10), "Excluído", 1, ativo: false);

        var planejadas = (await ObterAsync(Periodo(Base, Base))).AtividadesPlanejadas;

        Assert.Equal(new DashboardAtividadesPlanejadasDto(Hoje, Hoje.AddDays(29), 2), planejadas);
    }

    [Fact]
    public async Task AtividadesPlanejadas_CadaPasseioContaUmaVez_InclusiveAlemDosCincoProximosEventos()
    {
        for (var i = 1; i <= 7; i++) await EventoAsync(Hoje.AddDays(i), $"Local {i}", 1);
        var campori = await EventoAsync(Hoje.AddDays(20), "Campori", 3);
        await EventoAsync(Hoje.AddDays(20), "Campori", 2, grupoId: campori);
        await EventoAsync(Hoje.AddDays(20), "Campori", 2, grupoId: campori, ativo: false);
        var trilha = await EventoAsync(Hoje.AddDays(25), "Trilha", 1, ativo: false);
        await EventoAsync(Hoje.AddDays(25), "Trilha", 1, grupoId: trilha);
        var divisa = await EventoAsync(Hoje.AddDays(29), "Divisa", 1);
        await EventoAsync(Hoje.AddDays(30), "Divisa", 1, grupoId: divisa);

        var dashboard = await ObterAsync();

        Assert.Equal(10, dashboard.AtividadesPlanejadas.Quantidade);
        Assert.Equal(Almirante.Api.Services.DashboardService.LimiteProximosEventos, dashboard.ProximosEventos.Count);
    }

    [Fact]
    public async Task AtividadesRecentes_UltimosTrintaDiasAntesDeHoje_ExcluiHojeAntigosEExcluidos()
    {
        var ontem = await EventoAsync(Hoje.AddDays(-1), "Clube", 2, participantesInativos: 1, titulo: "Reunião");
        var limite = await EventoAsync(Hoje.AddDays(-30), "Acampamento", 1);
        await EventoAsync(Hoje.AddDays(-31), "Antigo", 1);
        await EventoAsync(Hoje, "Hoje", 1);
        await EventoAsync(Hoje.AddDays(-5), "Excluído", 1, ativo: false);

        var recentes = (await ObterAsync(Periodo(Base, Base))).AtividadesRecentes;

        Assert.Equal(new[] { ontem, limite }, recentes.Select(r => r.EventoGrupoId));
        Assert.Equal((Hoje.AddDays(-1), "Reunião", "Clube", 2),
            (recentes[0].Data, recentes[0].Titulo, recentes[0].Local, recentes[0].QuantidadeParticipantes));
        Assert.Equal(new[] { ontem }, recentes[0].EventoIds);
        Assert.Equal((Hoje.AddDays(-30), "Acampamento"), (recentes[1].Data, recentes[1].Local));
    }

    [Fact]
    public async Task AtividadesRecentes_MaisRecentesPrimeiro_DesempatePorLocal_ELimitadasACinco()
    {
        for (var i = 1; i <= 7; i++) await EventoAsync(Hoje.AddDays(-i), $"Local {i}", 1);
        await EventoAsync(Hoje.AddDays(-1), "A local", 1);

        var recentes = (await ObterAsync()).AtividadesRecentes;

        Assert.Equal(Almirante.Api.Services.DashboardService.LimiteAtividadesRecentes, recentes.Count);
        Assert.Equal(new[] { "A local", "Local 1", "Local 2", "Local 3", "Local 4" }, recentes.Select(r => r.Local));
        Assert.Equal(new[] { -1, -1, -2, -3, -4 }.Select(d => Hoje.AddDays(d)), recentes.Select(r => r.Data));
        Assert.Equal(recentes.Select(r => r.EventoGrupoId), (await ObterAsync()).AtividadesRecentes.Select(r => r.EventoGrupoId));
    }

    [Fact]
    public async Task AtividadesRecentes_PasseioRepetidoApareceUmaVez_SomandoCadastrosAtivos()
    {
        var data = Hoje.AddDays(-3);
        var raiz = await EventoAsync(data, "Campori", 4, titulo: "Campori 2026");
        var segundo = await EventoAsync(data, "Campori", 2, grupoId: raiz, titulo: "Campori 2026 - Unidade");
        await EventoAsync(data, "Campori", 9, grupoId: raiz, ativo: false, titulo: "Aaa");

        var evento = Assert.Single((await ObterAsync()).AtividadesRecentes);

        Assert.Equal((raiz, data, "Campori 2026", "Campori", 6),
            (evento.EventoGrupoId, evento.Data, evento.Titulo, evento.Local, evento.QuantidadeParticipantes));
        Assert.Equal(new[] { raiz, segundo }.Order(), evento.EventoIds);
    }

    [Fact]
    public async Task AtividadesRecentes_CadastrosDoPasseioEmDatasDiferentes_UsaAMaisRecenteDentroDaJanela()
    {
        var raiz = await EventoAsync(Hoje.AddDays(-10), "Trilha", 1);
        await EventoAsync(Hoje.AddDays(-2), "Trilha", 1, grupoId: raiz);
        await EventoAsync(Hoje.AddDays(4), "Trilha", 1, grupoId: raiz);

        var dashboard = await ObterAsync();

        var recente = Assert.Single(dashboard.AtividadesRecentes);
        Assert.Equal((raiz, Hoje.AddDays(-2), 3), (recente.EventoGrupoId, recente.Data, recente.QuantidadeParticipantes));
        Assert.Equal(Hoje.AddDays(4), Assert.Single(dashboard.ProximosEventos).Data);
        Assert.Equal(1, dashboard.AtividadesPlanejadas.Quantidade);
    }

    [Theory]
    [InlineData("?dataInicial=2025-03-01")]
    [InlineData("?dataFinal=2025-03-31")]
    [InlineData("?dataInicial=2025-03-31&dataFinal=2025-03-01")]
    [InlineData("?dataInicial=2025-01-01&dataFinal=2026-01-02")]
    [InlineData("?dataInicial=01/03/2025&dataFinal=2025-03-31")]
    [InlineData("?dataInicial=2025-3-1&dataFinal=2025-03-31")]
    [InlineData("?dataInicial=2025-02-30&dataFinal=2025-03-31")]
    [InlineData("?dataInicial=2025-03-01T00:00:00&dataFinal=2025-03-31")]
    [InlineData("?dataInicial=abc&dataFinal=2025-03-31")]
    [InlineData("?dataInicial=&dataFinal=2025-03-31")]
    public async Task FiltrosInvalidos_Retornam400(string query)
    {
        var resposta = await _client.GetAsync("/api/Dashboard" + query);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.NotEmpty(json.RootElement.GetProperty("errors").EnumerateObject());
    }

    [Fact]
    public async Task PeriodoDeUmAnoCompleto_EhAceito()
    {
        var dashboard = await ObterAsync("?dataInicial=2024-01-01&dataFinal=2024-12-31");

        Assert.Equal(new DashboardPeriodoDto(new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31)), dashboard.Periodo);
    }

    [Fact]
    public async Task Seguranca_SemTokenRetorna401_CargoSemPermissaoRetorna403_GestaoRetorna200()
    {
        using var anonimo = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/Dashboard")).StatusCode);

        foreach (var role in new[] { "DS", "CONS", "CPT", "INST", "TESU" })
        {
            var (cliente, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, role);
            var negado = await cliente.GetAsync("/api/Dashboard");
            Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
            Assert.DoesNotContain("financeiro", await negado.Content.ReadAsStringAsync());
        }

        foreach (var role in new[] { "DIR", "DIRA", "SEC", "TES" })
        {
            var (cliente, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, role);
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/Dashboard")).StatusCode);
        }
    }

    [Theory]
    [InlineData("ADM", true)] [InlineData("DIR", true)] [InlineData("DIRA", true)] [InlineData("TES", true)] [InlineData("SEC", false)]
    public async Task Financeiro_SoParaQuemTemGestaoFinanceira_DemaisIndicadoresIguaisParaTodos(string role, bool veFinanceiro)
    {
        await PagoAsync(TipoFluxoLancamento.Entrada, 80m, Hoje);
        await PagoAsync(TipoFluxoLancamento.Saida, 30m, Hoje);
        await TestHelpers.AddUsuarioAsync(_factory, "Desbravador contado", "DS");
        await EventoAsync(Hoje.AddDays(3), "Acampamento", participantes: 2);
        await EventoAsync(Hoje.AddDays(-3), "Trilha", participantes: 1);
        var (cliente, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, role);
        var completo = await ObterAsync();

        var resposta = await cliente.GetAsync("/api/Dashboard");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        var dashboard = JsonSerializer.Deserialize<DashboardResponse>(texto, Json)!;
        Assert.Equal(veFinanceiro ? new DashboardFinanceiroDto(50m, 80m, 30m) : null, dashboard.Financeiro);
        Assert.Equal(veFinanceiro, texto.Contains("\"saldo\"") || texto.Contains("\"entradas\"") || texto.Contains("\"saidas\""));
        Assert.Equal((completo.Periodo, completo.Desbravadores), (dashboard.Periodo, dashboard.Desbravadores));
        Assert.Equal(completo.ProximosEventos.Select(e => (e.EventoGrupoId, e.QuantidadeParticipantes)),
            dashboard.ProximosEventos.Select(e => (e.EventoGrupoId, e.QuantidadeParticipantes)));
        Assert.Equal(completo.AtividadesPlanejadas, dashboard.AtividadesPlanejadas);
        Assert.Equal(completo.AtividadesRecentes.Select(e => (e.EventoGrupoId, e.Data, e.Titulo, e.Local)),
            dashboard.AtividadesRecentes.Select(e => (e.EventoGrupoId, e.Data, e.Titulo, e.Local)));
        Assert.Equal((1, 1, 1, 1), (dashboard.Desbravadores.QuantidadeAtivos, dashboard.ProximosEventos.Count,
            dashboard.AtividadesPlanejadas.Quantidade, dashboard.AtividadesRecentes.Count));
    }
}
