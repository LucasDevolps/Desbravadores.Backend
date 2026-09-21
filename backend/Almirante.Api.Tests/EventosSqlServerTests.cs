using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Almirante.Api.Tests;

// Fluxos completos de /api/Eventos contra SQL Server REAL (transação, rowversion, índices únicos, triggers,
// SESSION_CONTEXT, concorrência). O InMemory não comprova nenhum desses comportamentos.
// Ver SqlServerApiFactory (ALMIRANTE_TEST_SQLSERVER): um banco descartável por fixture, com todas as migrations.
public sealed class EventosSqlFixture : IAsyncLifetime
{
    public SqlServerApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid AdminId { get; private set; }

    public async Task InitializeAsync()
    {
        Factory = new SqlServerApiFactory();
        Client = await TestHelpers.CreateAuthenticatedClientAsync(Factory);
        AdminId = await TestHelpers.WithDbAsync(Factory, db =>
            db.Usuarios.Where(u => u.Email == AlmiranteApiFactory.AdminEmail).Select(u => u.Id).SingleAsync());
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

[Trait("Category", "RequiresSqlServer")]
public sealed class EventosSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    // ------------------------------------------------------------------ apoio

    private async Task<List<Guid>> MembrosAsync(int quantidade)
    {
        var lista = new List<Guid>();
        for (var i = 0; i < quantidade; i++)
            lista.Add((await TestHelpers.AddUsuarioAsync(Factory, $"Membro evento {Guid.NewGuid():N}", "DS")).Id);
        return lista;
    }

    private async Task<EventoDto> CriarAsync(object? membros, HttpStatusCode esperado = HttpStatusCode.Created, string? chave = null, string? remoteIp = null, params (string Campo, object? Valor)[] ajustes)
    {
        var corpo = EventosTestKit.Corpo(membros);
        foreach (var (campo, valor) in ajustes) corpo[campo] = valor;
        var response = await EventosTestKit.PostAsync(Client, corpo, chave, remoteIp: remoteIp);
        Assert.True(esperado == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await EventosTestKit.LerAsync(response);
    }

    private static Dictionary<string, object?> Editar(EventoDto e, object? membros, decimal? transporte = null, string? local = null,
        DateOnly? data = null, string? motivo = null, string? versao = null, decimal? alimentacao = null, bool? individual = null, decimal? seguro = null) =>
        EventosTestKit.Corpo(membros, data ?? e.DataEvento, local ?? e.Local, transporte ?? e.Transporte.Valor, e.Transporte.EhGratis,
            alimentacao ?? e.Alimentacao.Valor, individual ?? e.Alimentacao.Individual, seguro ?? e.SeguroObrigatorio,
            versao: versao ?? e.Versao, motivo: motivo);

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<List<Lancamento>> LancamentosDe(Guid eventoId) =>
        Db(db => db.Lancamentos.AsNoTracking().Where(l => l.EventoId == eventoId).ToListAsync());

    private async Task<EventoDto> ObterAsync(Guid id) =>
        (await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{id}"))!;

    private async Task PagarAsync(Guid lancamentoId, string status = "Pago")
    {
        var response = await Client.PutAsJsonAsync($"/api/Lancamentos/{lancamentoId}", new { status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<SqlConnection> ConexaoAsync()
    {
        var conexao = new SqlConnection(Factory.ConnectionString);
        await conexao.OpenAsync();
        return conexao;
    }

    private async Task<int> ExecAsync(SqlConnection conexao, string sql, params (string, object)[] parametros)
    {
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return await comando.ExecuteNonQueryAsync();
    }

    private async Task<T?> EscalarAsync<T>(string sql, params (string, object)[] parametros)
    {
        await using var conexao = await ConexaoAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? default : (T)resultado;
    }

    private static async Task<int> NumeroDoErroAsync(Func<Task> acao) => (await Assert.ThrowsAsync<SqlException>(acao)).Number;

    // ------------------------------------------------------------------ POST: criação e cálculo

    [Fact]
    public async Task Post_GuidUnico_CriaEventoELancamentoConsolidado()
    {
        var m = await MembrosAsync(1);
        var response = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[0]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await EventosTestKit.LerAsync(response);

        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/Eventos/{dto.Id}", response.Headers.Location!.ToString());
        Assert.Equal(dto.Id, dto.EventoGrupoId);
        Assert.Null(dto.EventoReferenciaId);
        Assert.Equal([m[0]], dto.Membros);       // resposta sempre array
        Assert.Equal(1, dto.QuantidadeMembros);
        Assert.Equal(20m, dto.ValorPorMembro);
        Assert.Equal(20m, dto.Total);
        Assert.True(dto.Ativo);
        Assert.False(string.IsNullOrEmpty(dto.Versao));
        Assert.Equal(new TransporteDto(10m, false), dto.Transporte);
        Assert.Equal(new AlimentacaoDto(false, 8m), dto.Alimentacao);
        var resumo = Assert.Single(dto.Lancamentos);
        Assert.Equal((m[0], 20m, StatusLancamento.Pendente), (resumo.MembroId, resumo.Valor, resumo.Status));

        var lanc = Assert.Single(await LancamentosDe(dto.Id));
        Assert.Equal(m[0], lanc.MembroId);
        Assert.Null(lanc.Finalidade);              // null real, nunca "" nem "null"
        Assert.Equal(CategoriaLancamento.Evento, lanc.Categoria);
        Assert.Equal(StatusLancamento.Pendente, lanc.Status);
        Assert.Equal(TipoFluxoLancamento.Entrada, lanc.TipoFluxo);
        Assert.Equal(dto.DataEvento, lanc.Vencimento);
        Assert.True(lanc.Ativo);
        Assert.Equal(20m, lanc.Valor);
        Assert.Equal($"Evento - Parque Ibirapuera - {dto.DataEvento:dd/MM/yyyy}", lanc.Descricao);
        Assert.Null(await EscalarAsync<string>("SELECT Finalidade FROM Lancamentos WHERE Id = @id", ("@id", lanc.Id)));
    }

    [Fact]
    public async Task Post_ArrayComVariosMembros_UmLancamentoPorMembroComValorPorMembro_NuncaOTotal()
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(m);

        Assert.Equal(3, dto.QuantidadeMembros);
        Assert.Equal(20m, dto.ValorPorMembro);
        Assert.Equal(60m, dto.Total);
        var lancamentos = await LancamentosDe(dto.Id);
        Assert.Equal(3, lancamentos.Count);
        Assert.All(lancamentos, l => Assert.Equal(20m, l.Valor));
        Assert.Equal(m.OrderBy(x => x), lancamentos.Select(l => l.MembroId!.Value).OrderBy(x => x));
        Assert.Equal(m.OrderBy(x => x), dto.Membros);
        Assert.Equal(60m, lancamentos.Sum(l => l.Valor));
    }

    [Fact]
    public async Task Post_ExemploDaIssue_DoisMembros_QuarentaReais()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);
        Assert.Equal((20m, 40m), (dto.ValorPorMembro, dto.Total));
        Assert.Equal(2, (await LancamentosDe(dto.Id)).Count);
    }

    [Fact]
    public async Task Post_TransporteGratisEAlimentacaoIndividual_IgnoramSeusValores_ENormalizamParaZero()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, esperado: HttpStatusCode.Created, ajustes:
        [
            ("transporte", new { valor = 999m, ehGratis = true }),
            ("alimentacao", new { individual = true, valor = 777m }),
        ]);

        Assert.Equal(new TransporteDto(0m, true), dto.Transporte);
        Assert.Equal(new AlimentacaoDto(true, 0m), dto.Alimentacao);
        Assert.Equal(2m, dto.ValorPorMembro);
        Assert.Equal(4m, dto.Total);
        Assert.All(await LancamentosDe(dto.Id), l => Assert.Equal(2m, l.Valor));
        var linha = await Db(db => db.Eventos.AsNoTracking().SingleAsync(e => e.Id == dto.Id));
        Assert.Equal((0m, 0m), (linha.TransporteValor, linha.AlimentacaoValor));
    }

    [Fact]
    public async Task Post_AlimentacaoColetiva_ESeguroZero()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, ajustes: [("seguroObrigatorio", 0m)]);
        Assert.Equal((18m, 36m), (dto.ValorPorMembro, dto.Total));
    }

    [Fact]
    public async Task Post_TodosOsComponentesZerados_GeraLancamentosPendentesDeZeroParaCadaParticipante()
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(m, ajustes:
        [
            ("transporte", new { valor = 0m, ehGratis = false }),
            ("alimentacao", new { individual = false, valor = 0m }),
            ("seguroObrigatorio", 0m),
        ]);

        Assert.Equal((0m, 0m), (dto.ValorPorMembro, dto.Total));
        var lancamentos = await LancamentosDe(dto.Id);
        Assert.Equal(3, lancamentos.Count);
        Assert.All(lancamentos, l => Assert.Equal((0m, StatusLancamento.Pendente, true), (l.Valor, l.Status, l.Ativo)));
        Assert.Contains(dto.Id, (await Client.GetFromJsonAsync<EventosResponse>("/api/Eventos"))!.Items.Select(i => i.Id));
    }

    [Theory]
    [InlineData(0)]   // primeiro dia do mês
    [InlineData(10)]
    [InlineData(400)]
    public async Task Post_DatasValidas_PrimeiroDiaDoMesEFuturo(int deslocamentoDias)
    {
        var data = EventosTestKit.PrimeiroDiaDoMes.AddDays(deslocamentoDias);
        var dto = await CriarAsync((await MembrosAsync(1))[0], ajustes: [("dataEvento", data.ToString("yyyy-MM-dd"))]);
        Assert.Equal(data, dto.DataEvento);
        Assert.Equal(data, Assert.Single(await LancamentosDe(dto.Id)).Vencimento);
    }

    [Fact]
    public async Task Post_DataNoPrimeiroDiaDoMes_NasceComLancamentoPendente_NuncaPagoOuAtrasado()
    {
        // só é possível "dentro do mês" antes de hoje quando hoje não é o dia 1
        var data = EventosTestKit.PrimeiroDiaDoMes;
        var dto = await CriarAsync((await MembrosAsync(1))[0], ajustes: [("dataEvento", data.ToString("yyyy-MM-dd"))]);
        Assert.Equal(StatusLancamento.Pendente, Assert.Single(await LancamentosDe(dto.Id)).Status);
    }

    [Fact]
    public async Task Post_MembroInexistente_Retorna400ListandoIds_SemGravacaoParcial()
    {
        var existente = (await MembrosAsync(1))[0];
        var fantasma = Guid.NewGuid();
        var antes = (await Db(db => db.Eventos.CountAsync()), await Db(db => db.Lancamentos.CountAsync()), await Db(db => db.EventosOperacoes.CountAsync()));
        var chave = Guid.NewGuid().ToString();

        var response = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(new[] { existente, fantasma }), chave);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(fantasma.ToString(), await response.Content.ReadAsStringAsync());
        Assert.Equal(antes, (await Db(db => db.Eventos.CountAsync()), await Db(db => db.Lancamentos.CountAsync()), await Db(db => db.EventosOperacoes.CountAsync())));

        // retry após falha: a chave não foi consumida pela tentativa que falhou
        var retry = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(new[] { existente }), chave);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Post_GuidUnicoInexistente_Retorna400()
    {
        var response = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_CamposDoServidorEnviadosNoJson_SaoIgnoradosNaGravacao()
    {
        var m = (await MembrosAsync(1))[0];
        var falso = Guid.NewGuid();
        var dto = await CriarAsync(m, ajustes:
        [
            ("status", "Pago"), ("categoria", "Clube"), ("finalidade", "Mensalidade"), ("ativo", false), ("total", 1m),
            ("valorPorMembro", 1m), ("eventoId", falso), ("id", falso), ("eventoGrupoId", falso), ("usuarioResponsavelId", falso),
            ("ipResponsavel", "6.6.6.6"), ("versao", "AAAAAAAAB9E="),
        ]);

        Assert.NotEqual(falso, dto.Id);
        Assert.NotEqual(falso, dto.EventoGrupoId);
        Assert.True(dto.Ativo);
        Assert.Equal((20m, 20m), (dto.ValorPorMembro, dto.Total));
        var lanc = Assert.Single(await LancamentosDe(dto.Id));
        Assert.Equal((StatusLancamento.Pendente, CategoriaLancamento.Evento, (string?)null, true, 20m, dto.Id),
            (lanc.Status, lanc.Categoria, lanc.Finalidade, lanc.Ativo, lanc.Valor, lanc.EventoId!.Value));
        var linha = await Db(db => db.Eventos.AsNoTracking().SingleAsync(e => e.Id == dto.Id));
        Assert.Equal(fx.AdminId, linha.CriadoPorUsuarioId);
    }

    // ------------------------------------------------------------------ idempotência

    [Fact]
    public async Task Idempotencia_GuidUnicoEArrayDeUm_SaoAMesmaOperacao_Retorna200ComOResultadoOriginal()
    {
        var m = (await MembrosAsync(1))[0];
        var chave = Guid.NewGuid().ToString();
        var original = await CriarAsync(m, chave: chave);

        var replay = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(new[] { m }), chave);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var dto = await EventosTestKit.LerAsync(replay);
        Assert.Equal(original.Id, dto.Id);
        Assert.Equal(original.Versao, dto.Versao);
        Assert.Single(await LancamentosDe(original.Id));
        Assert.Equal(1, await Db(db => db.EventosOperacoes.CountAsync(o => o.EventoId == original.Id)));
    }

    [Fact]
    public async Task Idempotencia_MembrosEmOrdemDiferente_SaoAMesmaOperacao()
    {
        var m = await MembrosAsync(3);
        var chave = Guid.NewGuid().ToString();
        var original = await CriarAsync(m, chave: chave);

        var replay = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(Enumerable.Reverse(m).ToArray()), chave);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original.Id, (await EventosTestKit.LerAsync(replay)).Id);
        Assert.Equal(3, (await LancamentosDe(original.Id)).Count);
    }

    [Fact]
    public async Task Idempotencia_ValoresIgnoradosPelosBooleanos_SaoAMesmaOperacao()
    {
        var m = (await MembrosAsync(1))[0];
        var chave = Guid.NewGuid().ToString();
        var original = await CriarAsync(m, chave: chave, ajustes: [("transporte", new { valor = 10m, ehGratis = true })]);
        var replay = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m, transporte: 55m, gratis: true), chave);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original.Id, (await EventosTestKit.LerAsync(replay)).Id);
    }

    [Fact]
    public async Task Idempotencia_MesmaChaveComDadosDiferentes_Retorna409_SemNovosRegistros()
    {
        var m = await MembrosAsync(2);
        var chave = Guid.NewGuid().ToString();
        var original = await CriarAsync(m[0], chave: chave);
        var eventos = await Db(db => db.Eventos.CountAsync());

        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[1]), chave)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[0], seguro: 3m), chave)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[0], local: "Outro"), chave)).StatusCode);
        Assert.Equal(eventos, await Db(db => db.Eventos.CountAsync()));
        Assert.Single(await LancamentosDe(original.Id));
    }

    [Fact]
    public async Task Idempotencia_ChaveEhEscopadaPorUsuario()
    {
        var m = (await MembrosAsync(2));
        var chave = Guid.NewGuid().ToString();
        var doAdmin = await CriarAsync(m[0], chave: chave);

        var (outro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "TES");
        var resposta = await EventosTestKit.PostAsync(outro, EventosTestKit.Corpo(m[1]), chave);

        // outro usuário com a mesma chave NÃO recebe a resposta alheia nem conflita: cria o próprio evento
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.NotEqual(doAdmin.Id, (await EventosTestKit.LerAsync(resposta)).Id);
    }

    [Fact]
    public async Task Idempotencia_RequisicoesConcorrentesComAMesmaChave_CriamUmSoEvento()
    {
        var m = await MembrosAsync(4);
        var chave = Guid.NewGuid().ToString();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m), chave)));

        Assert.All(respostas, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        var ids = (await Task.WhenAll(respostas.Select(EventosTestKit.LerAsync))).Select(e => e.Id).Distinct().ToList();
        var id = Assert.Single(ids);
        Assert.Equal(4, (await LancamentosDe(id)).Count);
        Assert.Equal(1, await Db(db => db.EventosOperacoes.CountAsync(o => o.IdempotencyKey == chave)));
        Assert.Equal(4, await Db(db => db.Lancamentos.CountAsync(l => l.MembroId != null && m.Contains(l.MembroId.Value))));
    }

    [Fact]
    public async Task Idempotencia_DuasChavesDiferentesParaOMesmoMembroNoMesmoGrupo_SoUmaCobra()
    {
        var m = await MembrosAsync(3);
        var primeiro = await CriarAsync(new[] { m[0], m[1] });
        var alvo = m[2];

        var respostas = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(alvo, referencia: primeiro.Id, transporte: 3m, individual: true))));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], respostas.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await Db(db => db.EventosMembros.CountAsync(x => x.EventoGrupoId == primeiro.EventoGrupoId && x.MembroId == alvo && x.Ativo)));
        Assert.Equal(1, await Db(db => db.Lancamentos.CountAsync(l => l.MembroId == alvo && l.Ativo)));
        var conflito = respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains(alvo.ToString(), await conflito.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Idempotencia_ReenvioDeEventoJaExcluido_Retorna409_SemReativar()
    {
        var m = (await MembrosAsync(1))[0];
        var chave = Guid.NewGuid().ToString();
        var dto = await CriarAsync(m, chave: chave);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, "cancelado", dto.Versao)).StatusCode);

        var replay = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m), chave);

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.False(await Db(db => db.Eventos.Where(e => e.Id == dto.Id).Select(e => e.Ativo).SingleAsync()));
        Assert.All(await LancamentosDe(dto.Id), l => Assert.False(l.Ativo));
    }

    // ------------------------------------------------------------------ passeio com grupos de preços diferentes

    [Fact]
    public async Task Referencia_Ibirapuera_OitoVezesVinteMaisDoisVezesCinco_TotalizaCentoESetentaSemAlterarOPrimeiroGrupo()
    {
        var m = await MembrosAsync(10);
        var data = EventosTestKit.Hoje.AddDays(35);
        var local = $"Ibirapuera {Guid.NewGuid():N}";
        var primeiro = await CriarAsync(m.Take(8).ToArray(), ajustes: [("dataEvento", data.ToString("yyyy-MM-dd")), ("local", local)]);
        var versaoAntes = primeiro.Versao;

        var segundo = await CriarAsync(m.Skip(8).ToArray(), ajustes:
        [
            ("dataEvento", data.ToString("yyyy-MM-dd")), ("local", local), ("eventoReferenciaId", primeiro.Id),
            ("transporte", new { valor = 3m, ehGratis = false }), ("alimentacao", new { individual = true, valor = 0m }), ("seguroObrigatorio", 2m),
        ]);

        Assert.Equal(primeiro.EventoGrupoId, segundo.EventoGrupoId);
        Assert.Equal(primeiro.Id, segundo.EventoReferenciaId);
        Assert.NotEqual(primeiro.Id, segundo.Id);
        Assert.Equal((5m, 10m), (segundo.ValorPorMembro, segundo.Total));
        Assert.Equal((160m, 20m), ((await ObterAsync(primeiro.Id)).Total, (await ObterAsync(primeiro.Id)).ValorPorMembro));
        Assert.Equal(versaoAntes, (await ObterAsync(primeiro.Id)).Versao);
        Assert.All(await LancamentosDe(primeiro.Id), l => Assert.Equal(20m, l.Valor));
        Assert.All(await LancamentosDe(segundo.Id), l => Assert.Equal(5m, l.Valor));

        var lista = await Client.GetFromJsonAsync<EventosResponse>($"/api/Eventos?dataInicial={data:yyyy-MM-dd}&dataFinal={data:yyyy-MM-dd}");
        var doPasseio = lista!.Items.Where(i => i.EventoGrupoId == primeiro.EventoGrupoId).ToList();
        Assert.Equal(2, doPasseio.Count);
        Assert.Equal(170m, doPasseio.Sum(i => i.Total));
        Assert.Equal(10, doPasseio.Sum(i => i.QuantidadeMembros));
        Assert.All(doPasseio.SelectMany(i => i.Lancamentos), l => Assert.Equal(StatusLancamento.Pendente, l.Status));
    }

    [Fact]
    public async Task Referencia_MembroJaEmOutroGrupoDoPasseio_Retorna409ComOsIds_SemGravacaoParcial()
    {
        var m = await MembrosAsync(3);
        var primeiro = await CriarAsync(new[] { m[0], m[1] });
        var lancamentos = await Db(db => db.Lancamentos.CountAsync());

        var response = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(new[] { m[1], m[2] }, referencia: primeiro.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains(m[1].ToString(), corpo);
        Assert.DoesNotContain(m[2].ToString(), corpo);
        Assert.Equal(lancamentos, await Db(db => db.Lancamentos.CountAsync()));
        Assert.Equal(0, await Db(db => db.EventosMembros.CountAsync(x => x.MembroId == m[2])));
    }

    [Fact]
    public async Task Referencia_InexistenteOuInativa_Retorna404()
    {
        var m = await MembrosAsync(2);
        Assert.Equal(HttpStatusCode.NotFound, (await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[0], referencia: Guid.NewGuid()))).StatusCode);

        var excluido = await CriarAsync(m[0]);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, excluido.Id, "x", excluido.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[1], referencia: excluido.Id))).StatusCode);
    }

    [Fact]
    public async Task Referencia_DataOuLocalDivergentes_Retorna400PorCampo()
    {
        var m = await MembrosAsync(3);
        var primeiro = await CriarAsync(m[0]);

        var data = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[1], data: primeiro.DataEvento.AddDays(1), referencia: primeiro.Id));
        Assert.Equal(HttpStatusCode.BadRequest, data.StatusCode);
        Assert.Contains("DataEvento", (await data.Content.ReadAsStringAsync()));

        var local = await EventosTestKit.PostAsync(Client, EventosTestKit.Corpo(m[1], local: "Outro parque", referencia: primeiro.Id));
        Assert.Equal(HttpStatusCode.BadRequest, local.StatusCode);
        Assert.Contains("Local", (await local.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Referencia_SegundoGrupoSobreviveExclusaoDoPrimeiro_EMantemOMesmoGrupo()
    {
        var m = await MembrosAsync(3);
        var primeiro = await CriarAsync(new[] { m[0], m[1] });
        var segundo = await CriarAsync(m[2], ajustes: [("eventoReferenciaId", primeiro.Id)]);

        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, primeiro.Id, "cancelado", primeiro.Versao)).StatusCode);

        Assert.True((await ObterAsync(segundo.Id)).Ativo);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/Eventos/{primeiro.Id}")).StatusCode);
        Assert.Single(await LancamentosDe(segundo.Id), l => l.Ativo);
    }

    // ------------------------------------------------------------------ GET

    [Fact]
    public async Task Get_FiltroInclusivoOrdenadoPorDataLocalId_ExcluiInativos()
    {
        var m = await MembrosAsync(4);
        var baseData = new DateOnly(2036, 5, 10);
        var sufixo = Guid.NewGuid().ToString("N")[..6];
        var b = await CriarAsync(m[0], ajustes: [("dataEvento", baseData.ToString("yyyy-MM-dd")), ("local", $"B {sufixo}")]);
        var a = await CriarAsync(m[1], ajustes: [("dataEvento", baseData.ToString("yyyy-MM-dd")), ("local", $"A {sufixo}")]);
        var antes = await CriarAsync(m[2], ajustes: [("dataEvento", baseData.AddDays(-1).ToString("yyyy-MM-dd")), ("local", $"Z {sufixo}")]);
        var fora = await CriarAsync(m[3], ajustes: [("dataEvento", baseData.AddDays(2).ToString("yyyy-MM-dd")), ("local", $"F {sufixo}")]);

        var inclusivo = await Client.GetFromJsonAsync<EventosResponse>($"/api/Eventos?dataInicial={baseData.AddDays(-1):yyyy-MM-dd}&dataFinal={baseData:yyyy-MM-dd}");
        Assert.Equal([antes.Id, a.Id, b.Id], inclusivo!.Items.Select(i => i.Id));
        Assert.DoesNotContain(fora.Id, inclusivo.Items.Select(i => i.Id));
        Assert.Equal(EventosTestKit.PrimeiroDiaDoMes, inclusivo.DataMinimaCadastro);

        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, a.Id, "x", a.Versao)).StatusCode);
        var depois = await Client.GetFromJsonAsync<EventosResponse>($"/api/Eventos?dataInicial={baseData.AddDays(-1):yyyy-MM-dd}&dataFinal={baseData:yyyy-MM-dd}");
        Assert.Equal([antes.Id, b.Id], depois!.Items.Select(i => i.Id));

        var vazio = await Client.GetFromJsonAsync<EventosResponse>("/api/Eventos?dataInicial=2001-01-01&dataFinal=2001-01-31");
        Assert.Empty(vazio!.Items);
    }

    [Fact]
    public async Task Get_SemParametros_ListaEventosDoPeriodoPadrao()
    {
        var dto = await CriarAsync((await MembrosAsync(1))[0]);
        var lista = await Client.GetFromJsonAsync<EventosResponse>("/api/Eventos");
        Assert.Contains(dto.Id, lista!.Items.Select(i => i.Id));
        Assert.Equal(EventosTestKit.PrimeiroDiaDoMes.AddDays(-30), lista.DataInicial);
    }

    // ------------------------------------------------------------------ PUT

    [Fact]
    public async Task Put_GuidUnico_MantemMembroEAtualizaCustos()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);

        var response = await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, transporte: 15m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var atualizado = await EventosTestKit.LerAsync(response);
        Assert.Equal((25m, 25m), (atualizado.ValorPorMembro, atualizado.Total));
        Assert.NotEqual(dto.Versao, atualizado.Versao);
        Assert.Equal(dto.Id, atualizado.Id);
        Assert.Equal(dto.EventoGrupoId, atualizado.EventoGrupoId);
        Assert.Equal(25m, Assert.Single(await LancamentosDe(dto.Id)).Valor);
        Assert.Equal(atualizado.Versao, (await ObterAsync(dto.Id)).Versao);
    }

    [Fact]
    public async Task Put_VariosMembros_AdicionaRemoveEMantem_SincronizandoLancamentos()
    {
        var m = await MembrosAsync(4);
        var dto = await CriarAsync(new[] { m[0], m[1], m[2] });
        var lancamentoMantido = dto.Lancamentos.Single(l => l.MembroId == m[0]).Id;
        var lancamentoRemovido = dto.Lancamentos.Single(l => l.MembroId == m[2]).Id;

        var response = await EventosTestKit.PutAsync(Client, dto.Id,
            Editar(dto, new[] { m[0], m[1], m[3] }, transporte: 12m, motivo: "Membro desistiu"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var novo = await EventosTestKit.LerAsync(response);
        Assert.Equal(m.Where(x => x != m[2]).OrderBy(x => x), novo.Membros);
        Assert.Equal((22m, 66m, 3), (novo.ValorPorMembro, novo.Total, novo.QuantidadeMembros));
        Assert.Equal(3, novo.Lancamentos.Count);
        Assert.All(novo.Lancamentos, l => Assert.Equal(22m, l.Valor));
        Assert.Contains(novo.Lancamentos, l => l.Id == lancamentoMantido); // mantido: mesmo lançamento, atualizado
        Assert.DoesNotContain(novo.Lancamentos, l => l.Id == lancamentoRemovido);

        var todos = await LancamentosDe(dto.Id);
        Assert.Equal(4, todos.Count);                                     // histórico preservado (sem exclusão física)
        var removido = todos.Single(l => l.Id == lancamentoRemovido);
        Assert.False(removido.Ativo);
        Assert.Equal(20m, removido.Valor);
        Assert.Equal(3, todos.Count(l => l.Ativo));
        var participacoes = await Db(db => db.EventosMembros.AsNoTracking().Where(p => p.EventoId == dto.Id).ToListAsync());
        Assert.Equal(4, participacoes.Count);
        Assert.False(participacoes.Single(p => p.MembroId == m[2]).Ativo);
        Assert.NotNull(participacoes.Single(p => p.MembroId == m[2]).DesativadoEmUtc);

        // remoção auditada com o motivo do PUT (trigger de Lancamentos)
        var auditoria = await Db(db => db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == lancamentoRemovido));
        Assert.Equal(("Membro desistiu", fx.AdminId, dto.Id), (auditoria.Motivo, auditoria.UsuarioResponsavelId, auditoria.EventoId));
        Assert.Null(auditoria.Finalidade);
    }

    [Fact]
    public async Task Put_RemoverParticipanteExigeMotivo_ENadaEAlteradoSemEle()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);

        var semMotivo = await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m[0]));
        Assert.Equal(HttpStatusCode.BadRequest, semMotivo.StatusCode);
        Assert.Contains("Motivo", await semMotivo.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m[0], motivo: new string('x', 256)))).StatusCode);

        Assert.Equal(2, (await LancamentosDe(dto.Id)).Count(l => l.Ativo));
        Assert.Equal(dto.Versao, (await ObterAsync(dto.Id)).Versao);
    }

    [Fact]
    public async Task Put_MesmoPutRepetido_NaoDuplicaLancamentos()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);
        var corpo = Editar(dto, new[] { m[0], m[1] }, transporte: 11m);

        var primeiro = await EventosTestKit.PutAsync(Client, dto.Id, corpo);
        var repetido = await EventosTestKit.PutAsync(Client, dto.Id, corpo); // versão agora desatualizada

        Assert.Equal(HttpStatusCode.OK, primeiro.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal(2, (await LancamentosDe(dto.Id)).Count);

        // reenvio com a versão nova e os mesmos dados: nada além da versão muda
        var atual = await EventosTestKit.LerAsync(primeiro);
        var mesmoConteudo = await EventosTestKit.PutAsync(Client, dto.Id, Editar(atual, new[] { m[0], m[1] }));
        Assert.Equal(HttpStatusCode.OK, mesmoConteudo.StatusCode);
        Assert.Equal(2, (await LancamentosDe(dto.Id)).Count);
    }

    [Fact]
    public async Task Put_VersaoIncorretaOuInexistenteOuExcluido()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);

        Assert.Equal(HttpStatusCode.Conflict,
            (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, transporte: 99m, versao: Convert.ToBase64String(new byte[8])))).StatusCode);
        Assert.Equal(20m, Assert.Single(await LancamentosDe(dto.Id)).Valor); // não sobrescreveu

        Assert.Equal(HttpStatusCode.NotFound, (await EventosTestKit.PutAsync(Client, Guid.NewGuid(), Editar(dto, m))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, "x", dto.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m))).StatusCode);
    }

    [Fact]
    public async Task Put_DoisPutsSimultaneosComAMesmaVersao_UmVenceEOutroRecebe409()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);

        var respostas = await Task.WhenAll(
            EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, transporte: 30m)),
            EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, transporte: 40m)));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], respostas.Select(r => r.StatusCode).Order());
        var vencedor = await EventosTestKit.LerAsync(respostas.Single(r => r.StatusCode == HttpStatusCode.OK));
        var lancamentos = await LancamentosDe(dto.Id);
        Assert.All(lancamentos, l => Assert.Equal(vencedor.ValorPorMembro, l.Valor)); // sem mistura das duas escritas
        Assert.Equal(vencedor.Versao, (await ObterAsync(dto.Id)).Versao);
    }

    [Fact]
    public async Task Put_MembroEmOutroGrupoDoPasseio_Retorna409()
    {
        var m = await MembrosAsync(3);
        var g1 = await CriarAsync(new[] { m[0], m[1] });
        var g2 = await CriarAsync(m[2], ajustes: [("eventoReferenciaId", g1.Id)]);

        var response = await EventosTestKit.PutAsync(Client, g2.Id, Editar(g2, new[] { m[2], m[0] }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(m[0].ToString(), await response.Content.ReadAsStringAsync());
        Assert.Single(await LancamentosDe(g2.Id), l => l.Ativo);
    }

    [Fact]
    public async Task Put_MembroInexistenteAdicionado_Retorna400()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, new[] { m, Guid.NewGuid() }))).StatusCode);
    }

    [Fact]
    public async Task Put_DataEmDoisCadastrosAtivos_BloqueiaDataELocal_MasPermiteCustos()
    {
        var m = await MembrosAsync(2);
        var g1 = await CriarAsync(m[0]);
        var g2 = await CriarAsync(m[1], ajustes: [("eventoReferenciaId", g1.Id)]);

        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, g1.Id, Editar(g1, m[0], local: "Outro local"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, g1.Id, Editar(g1, m[0], data: g1.DataEvento.AddDays(1)))).StatusCode);
        var custos = await EventosTestKit.PutAsync(Client, g1.Id, Editar(g1, m[0], transporte: 1m));
        Assert.Equal(HttpStatusCode.OK, custos.StatusCode);

        // depois de excluir o outro grupo, o cadastro remanescente pode mudar data e local
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, g2.Id, "x", g2.Versao)).StatusCode);
        var atual = await EventosTestKit.LerAsync(custos);
        var novaData = atual.DataEvento.AddDays(2);
        var ok = await EventosTestKit.PutAsync(Client, g1.Id, Editar(atual, m[0], local: "Outro local", data: novaData));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var alterado = await EventosTestKit.LerAsync(ok);
        Assert.Equal((novaData, "Outro local"), (alterado.DataEvento, alterado.Local));
        var lanc = Assert.Single(await LancamentosDe(g1.Id));
        Assert.Equal(novaData, lanc.Vencimento);
        Assert.Contains("Outro local", lanc.Descricao);
    }

    [Fact]
    public async Task Put_DataAlteradaAntesDoMesAtualEhRejeitada_MasDataHistoricaInalteradaEhPreservada()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        var mesPassado = EventosTestKit.PrimeiroDiaDoMes.AddDays(-5);

        var rejeitada = await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, data: mesPassado));
        Assert.Equal(HttpStatusCode.BadRequest, rejeitada.StatusCode);
        Assert.Contains("DataEvento", await rejeitada.Content.ReadAsStringAsync());

        // evento histórico (data no mês passado): correção de custos com a data original não é bloqueada
        await using (var conexao = await ConexaoAsync())
        {
            await ExecAsync(conexao, "UPDATE dbo.eventos SET DataEvento = @d WHERE Id = @id", ("@d", mesPassado.ToDateTime(TimeOnly.MinValue)), ("@id", dto.Id));
            await ExecAsync(conexao, "UPDATE dbo.Lancamentos SET Vencimento = @d WHERE EventoId = @id", ("@d", mesPassado.ToDateTime(TimeOnly.MinValue)), ("@id", dto.Id));
        }

        var historico = await ObterAsync(dto.Id);
        var corrigido = await EventosTestKit.PutAsync(Client, dto.Id, Editar(historico, m, transporte: 12m));
        Assert.Equal(HttpStatusCode.OK, corrigido.StatusCode);
        Assert.Equal(mesPassado, (await EventosTestKit.LerAsync(corrigido)).DataEvento);
    }

    // ------------------------------------------------------------------ proteção financeira

    [Theory]
    [InlineData("Pago")]
    [InlineData("Atrasado")]
    public async Task Put_ComLancamentoPagoOuAtrasado_BloqueiaValorParticipantesEData_PreservandoOPagamento(string status)
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(new[] { m[0], m[1] });
        var pago = dto.Lancamentos[0];
        await PagarAsync(pago.Id, status);
        dto = await ObterAsync(dto.Id); // versão mudou com o status

        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, dto.Membros, transporte: 99m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, new[] { m[0], m[1], m[2] }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, new[] { dto.Membros[0] }, motivo: "remover"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, dto.Membros, data: dto.DataEvento.AddDays(1)))).StatusCode);

        var lancamentos = await LancamentosDe(dto.Id);
        Assert.Equal(2, lancamentos.Count);                      // nada criado/removido/recriado
        Assert.All(lancamentos, l => Assert.Equal((20m, true), (l.Valor, l.Ativo)));
        Assert.Equal(Enum.Parse<StatusLancamento>(status), lancamentos.Single(l => l.Id == pago.Id).Status);
        Assert.Equal(StatusLancamento.Pendente, lancamentos.Single(l => l.Id != pago.Id).Status);
        Assert.Equal(dto.Versao, (await ObterAsync(dto.Id)).Versao);
    }

    [Fact]
    public async Task Put_ComLancamentoPago_PermiteCorrigirSoOLocal()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await PagarAsync(dto.Lancamentos[0].Id);
        dto = await ObterAsync(dto.Id);

        var response = await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, local: "Parque Ibirapuera (corrigido)"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lanc = Assert.Single(await LancamentosDe(dto.Id));
        Assert.Equal((StatusLancamento.Pago, 20m), (lanc.Status, lanc.Valor));
        // o lançamento pago não é tocado pela correção de local (descrição original preservada)
        Assert.Equal($"Evento - Parque Ibirapuera - {dto.DataEvento:dd/MM/yyyy}", lanc.Descricao);
    }

    [Fact]
    public async Task MudancaDeStatusDoLancamento_AlteraAVersaoDoEvento_EInvalidaPutComVersaoAnterior()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await PagarAsync(dto.Lancamentos[0].Id);
        var depois = await ObterAsync(dto.Id);

        Assert.NotEqual(dto.Versao, depois.Versao);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, transporte: 5m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.DeleteAsync(Client, dto.Id, "x", dto.Versao)).StatusCode);
    }

    // ------------------------------------------------------------------ DELETE

    [Fact]
    public async Task Delete_EventoPendente_DesativaTudoDoGrupo_PreservaOutrosGrupos_EGravaSnapshotPorTrigger()
    {
        var m = await MembrosAsync(3);
        var g1 = await CriarAsync(new[] { m[0], m[1] });
        var g2 = await CriarAsync(m[2], ajustes: [("eventoReferenciaId", g1.Id)]);

        var response = await EventosTestKit.DeleteAsync(Client, g1.Id, "Grupo cancelado pela diretoria", g1.Versao, remoteIp: "198.51.100.77");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await Db(db => db.Eventos.Where(e => e.Id == g1.Id).Select(e => e.Ativo).SingleAsync()));
        Assert.All(await Db(db => db.EventosMembros.Where(p => p.EventoId == g1.Id).ToListAsync()), p => Assert.False(p.Ativo));
        Assert.All(await LancamentosDe(g1.Id), l => Assert.False(l.Ativo));
        Assert.Equal(2, (await LancamentosDe(g1.Id)).Count);                 // nada apagado fisicamente
        Assert.True((await ObterAsync(g2.Id)).Ativo);                        // outro grupo preservado
        Assert.All(await LancamentosDe(g2.Id), l => Assert.True(l.Ativo));

        // histórico criado exclusivamente pelo trigger
        var historico = Assert.Single(await Db(db => db.HistoricoEventos.AsNoTracking().Where(h => h.EventoId == g1.Id).ToListAsync()));
        Assert.Equal((g1.EventoGrupoId, (Guid?)null, g1.DataEvento, "Parque Ibirapuera"), (historico.EventoGrupoId, historico.EventoReferenciaId, historico.DataEvento, historico.Local));
        Assert.Equal((false, 10m, false, 8m, 2m), (historico.TransporteEhGratis, historico.TransporteValor, historico.AlimentacaoIndividual, historico.AlimentacaoValor, historico.SeguroObrigatorio));
        Assert.Equal((20m, 2, 40m), (historico.ValorPorMembro, historico.QuantidadeMembros, historico.Total));
        Assert.Equal((fx.AdminId, "198.51.100.77", "Grupo cancelado pela diretoria"), (historico.UsuarioResponsavelId, historico.IpResponsavel, historico.Motivo));
        var relogioDoBanco = await EscalarAsync<DateTime>("SELECT SYSUTCDATETIME()");   // gerado pelo banco, não pela aplicação
        Assert.InRange(historico.ExcluidoEmUtc, relogioDoBanco.AddMinutes(-5), relogioDoBanco.AddSeconds(5));

        var participantes = JsonNode.Parse(historico.ParticipantesJson)!.AsArray();
        Assert.Equal(2, participantes.Count);
        Assert.Equal(m.Take(2).Select(x => x.ToString()).Order(), participantes.Select(p => ((string)p!["membroId"]!).ToLowerInvariant()).Order());
        Assert.Equal(g1.Lancamentos.Select(l => l.Id.ToString()).Order(), participantes.Select(p => ((string)p!["lancamentoId"]!).ToLowerInvariant()).Order());
        Assert.All(participantes, p => Assert.Equal(("Pendente", 20m), ((string)p!["status"]!, (decimal)p["valor"]!)));

        // auditoria dos lançamentos desativados (trigger existente), agora com EventoId e Finalidade null
        var auditoriaLanc = await Db(db => db.LancamentosDeletados.AsNoTracking().Where(d => d.EventoId == g1.Id).ToListAsync());
        Assert.Equal(2, auditoriaLanc.Count);
        Assert.All(auditoriaLanc, d => Assert.Equal((fx.AdminId, "198.51.100.77", "Grupo cancelado pela diretoria", (string?)null), (d.UsuarioResponsavelId, d.IpResponsavel, d.Motivo, d.Finalidade)));
    }

    [Fact]
    public async Task Delete_Inexistente_JaExcluido_E_SegundaExclusao_Retornam404_SemNovoHistorico()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);

        Assert.Equal(HttpStatusCode.NotFound, (await EventosTestKit.DeleteAsync(Client, Guid.NewGuid(), "x", dto.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, "primeira", dto.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EventosTestKit.DeleteAsync(Client, dto.Id, "segunda", dto.Versao)).StatusCode);

        Assert.Equal(1, await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == dto.Id)));
        Assert.Equal(1, await Db(db => db.LancamentosDeletados.CountAsync(d => d.EventoId == dto.Id)));
    }

    [Theory]
    [InlineData("Pago")]
    [InlineData("Atrasado")]
    public async Task Delete_ComLancamentoPagoOuAtrasado_Retorna409_SemAlterarNenhumRegistro(string status)
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);
        await PagarAsync(dto.Lancamentos[0].Id, status);
        dto = await ObterAsync(dto.Id);

        var response = await EventosTestKit.DeleteAsync(Client, dto.Id, "tentativa", dto.Versao);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await Db(db => db.Eventos.Where(e => e.Id == dto.Id).Select(e => e.Ativo).SingleAsync()));
        Assert.All(await Db(db => db.EventosMembros.Where(p => p.EventoId == dto.Id).ToListAsync()), p => Assert.True(p.Ativo));
        Assert.All(await LancamentosDe(dto.Id), l => Assert.True(l.Ativo));
        Assert.Equal(0, await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == dto.Id)));
        Assert.Equal(0, await Db(db => db.LancamentosDeletados.CountAsync(d => d.EventoId == dto.Id)));
        Assert.Equal(dto.Versao, (await ObterAsync(dto.Id)).Versao);   // rollback inclusive do "lock" de versão
    }

    [Fact]
    public async Task Delete_VersaoDesatualizada_Retorna409()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        var editado = await EventosTestKit.LerAsync(await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, transporte: 11m)));

        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.DeleteAsync(Client, dto.Id, "x", dto.Versao)).StatusCode);
        Assert.True((await ObterAsync(dto.Id)).Ativo);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, "x", editado.Versao)).StatusCode);
    }

    [Fact]
    public async Task Delete_DuasExclusoesSimultaneas_UmaVenceEAOutraRecebe404_UmUnicoHistorico()
    {
        var m = (await MembrosAsync(2));
        var dto = await CriarAsync(m);

        var respostas = await Task.WhenAll(
            EventosTestKit.DeleteAsync(Client, dto.Id, "a", dto.Versao),
            EventosTestKit.DeleteAsync(Client, dto.Id, "b", dto.Versao));

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NotFound], respostas.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == dto.Id)));
    }

    [Fact]
    public async Task Delete_ContextoDeAuditoriaNaoVazaParaOutrasConexoesDoPool()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, "vazamento", dto.Versao, remoteIp: "203.0.113.9")).StatusCode);

        for (var i = 0; i < 30; i++)
        {
            await using var conexao = await ConexaoAsync();
            await using var comando = conexao.CreateCommand();
            comando.CommandText = """
                SELECT CONCAT(CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS nvarchar(60)), '|',
                              CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS nvarchar(60)), '|',
                              CAST(SESSION_CONTEXT(N'MotivoExclusao') AS nvarchar(60)))
                """;
            Assert.Equal("||", (string)(await comando.ExecuteScalarAsync())!);
        }

        // uma exclusão sem contexto prévio (fora da aplicação) continua sendo recusada pelo trigger
        var outro = await CriarAsync((await MembrosAsync(1))[0]);
        await using var direta = await ConexaoAsync();
        Assert.Equal(50011, await NumeroDoErroAsync(() => ExecAsync(direta, "UPDATE dbo.eventos SET Ativo = 0 WHERE Id = @id", ("@id", outro.Id))));
    }

    [Fact]
    public async Task Concorrencia_PutsQueAdicionamOMesmoMembroEmGruposDiferentes_UmSoVence()
    {
        var m = await MembrosAsync(3);
        var g1 = await CriarAsync(m[0]);
        var g2 = await CriarAsync(m[1], ajustes: [("eventoReferenciaId", g1.Id)]);
        var alvo = m[2];

        var respostas = await Task.WhenAll(
            EventosTestKit.PutAsync(Client, g1.Id, Editar(g1, new[] { m[0], alvo })),
            EventosTestKit.PutAsync(Client, g2.Id, Editar(g2, new[] { m[1], alvo })));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], respostas.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await Db(db => db.EventosMembros.CountAsync(x => x.EventoGrupoId == g1.EventoGrupoId && x.MembroId == alvo && x.Ativo)));
        Assert.Equal(1, await Db(db => db.Lancamentos.CountAsync(l => l.MembroId == alvo && l.Ativo)));
        Assert.Contains(alvo.ToString(), await respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concorrencia_PagamentoEExclusaoSimultaneos_NaoTravamNemPerdemOPagamento()
    {
        for (var rodada = 0; rodada < 6; rodada++)
        {
            var m = await MembrosAsync(2);
            var dto = await CriarAsync(m);
            var lancamento = dto.Lancamentos[0].Id;

            var pagar = Client.PutAsJsonAsync($"/api/Lancamentos/{lancamento}", new { status = "Pago" });
            var excluir = EventosTestKit.DeleteAsync(Client, dto.Id, "corrida", dto.Versao);
            await Task.WhenAll(pagar, excluir);

            var pagou = (await pagar).StatusCode;
            var excluiu = (await excluir).StatusCode;
            Assert.DoesNotContain(HttpStatusCode.InternalServerError, new[] { pagou, excluiu });

            var lancamentos = await LancamentosDe(dto.Id);
            var ativo = await Db(db => db.Eventos.Where(e => e.Id == dto.Id).Select(e => e.Ativo).SingleAsync());
            if (excluiu == HttpStatusCode.NoContent)
            {
                // exclusão venceu: tudo desativado e o pagamento não pode ter "sobrevivido" numa linha ativa
                Assert.False(ativo);
                Assert.All(lancamentos, l => Assert.False(l.Ativo));
                Assert.Equal(1, await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == dto.Id)));
            }
            else
            {
                // pagamento venceu: a exclusão foi recusada (409) e nada mudou além do status
                Assert.Equal(HttpStatusCode.Conflict, excluiu);
                Assert.Equal(HttpStatusCode.OK, pagou);
                Assert.True(ativo);
                Assert.All(lancamentos, l => Assert.True(l.Ativo));
                Assert.Equal(StatusLancamento.Pago, lancamentos.Single(l => l.Id == lancamento).Status);
                Assert.Equal(0, await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == dto.Id)));
            }
        }
    }

    [Fact]
    public async Task Concorrencia_PagamentoEPutSimultaneos_ProtegemOPagamento()
    {
        for (var rodada = 0; rodada < 6; rodada++)
        {
            var m = await MembrosAsync(1);
            var dto = await CriarAsync(m);
            var lancamento = dto.Lancamentos[0].Id;

            var pagar = Client.PutAsJsonAsync($"/api/Lancamentos/{lancamento}", new { status = "Pago" });
            var editar = EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m, transporte: 77m));
            await Task.WhenAll(pagar, editar);

            Assert.Equal(HttpStatusCode.OK, (await pagar).StatusCode);
            var editou = (await editar).StatusCode;
            Assert.Contains(editou, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            var lanc = Assert.Single(await LancamentosDe(dto.Id));
            Assert.Equal(StatusLancamento.Pago, lanc.Status);
            // se o PUT venceu, ocorreu ANTES do pagamento; se perdeu, o valor pago continua o original
            Assert.Equal(editou == HttpStatusCode.OK ? 87m : 20m, lanc.Valor);
        }
    }

    // ------------------------------------------------------------------ lançamentos vinculados a eventos (regressão do módulo)

    [Fact]
    public async Task Lancamentos_VinculadoAEvento_BloqueiaAlteracaoIsoladaEExclusao_MasPermiteStatus()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        var id = dto.Lancamentos[0].Id;

        foreach (var corpo in new object[]
        {
            new { valor = 50m },
            new { vencimento = dto.DataEvento.AddDays(1).ToString("yyyy-MM-dd") },
            new { categoria = "Clube" },
            new { finalidade = "Mensalidade" },
            new { tipoFluxo = "Saida" },
        })
        {
            var response = await Client.PutAsJsonAsync($"/api/Lancamentos/{id}", corpo);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("/api/Eventos", await response.Content.ReadAsStringAsync());
        }

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{id}") { Content = JsonContent.Create(new { motivo = "isolada" }) };
        var excluido = await Client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.Conflict, excluido.StatusCode);
        Assert.Contains("/api/Eventos", await excluido.Content.ReadAsStringAsync());

        var lanc = Assert.Single(await LancamentosDe(dto.Id));
        Assert.Equal((20m, true, dto.DataEvento, CategoriaLancamento.Evento, (string?)null), (lanc.Valor, lanc.Ativo, lanc.Vencimento, lanc.Categoria, lanc.Finalidade));
        Assert.Equal(0, await Db(db => db.LancamentosDeletados.CountAsync(d => d.LancamentoId == id)));

        // status e descrição continuam no fluxo financeiro
        var status = await Client.PutAsJsonAsync($"/api/Lancamentos/{id}", new { status = "Pago", valor = 20m });
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var lido = await status.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.Equal((StatusLancamento.Pago, dto.Id, (string?)null), (lido!.Status, lido.EventoId, lido.Finalidade));
    }

    [Fact]
    public async Task Lancamentos_ConsultaExibeFinalidadeNulaEEventoId_ENaoQuebraABusca()
    {
        var m = (await MembrosAsync(1))[0];
        var local = $"Busca{Guid.NewGuid():N}";
        var dto = await CriarAsync(m, ajustes: [("local", local)]);

        var lista = await Client.GetFromJsonAsync<LancamentosResponse>($"/api/Lancamentos?search={local}&pageSize=50");
        var item = Assert.Single(lista!.Items);
        Assert.Equal((null, dto.Id, CategoriaLancamento.Evento, StatusLancamento.Pendente), (item.Finalidade, item.EventoId, item.Categoria, item.Status));
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/Lancamentos?search=mensalidade&status=Pendente&finalidade=Mensalidade")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/Lancamentos?status=Todos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/Lancamentos/{item.Id}")).StatusCode);
    }

    [Fact]
    public async Task Lancamentos_ExcecoesDeEventoNaoVazamParaORegistroAvulso()
    {
        var m = (await MembrosAsync(1))[0];
        object Corpo(string? finalidade, decimal valor, DateOnly vencimento) => new
        {
            membroId = m, finalidade, descricao = "avulso", categoria = "Evento", tipoFluxo = "Entrada", valor,
            vencimento = vencimento.ToString("yyyy-MM-dd"), aplicarATodosOsMembros = false,
        };
        var futuro = EventosTestKit.Hoje.AddDays(5);

        Assert.Equal(HttpStatusCode.Created, (await Client.PostAsJsonAsync("/api/Lancamentos/Registrar", Corpo("Mensalidade", 10m, futuro))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/Lancamentos/Registrar", Corpo(null, 10m, futuro))).StatusCode);   // finalidade nula
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/Lancamentos/Registrar", Corpo("", 10m, futuro))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/Lancamentos/Registrar", Corpo("Mensalidade", 0m, futuro))).StatusCode); // valor zero
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/Lancamentos/Registrar", Corpo("Mensalidade", 10m, EventosTestKit.Hoje.AddDays(-1)))).StatusCode); // data passada
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/Lancamentos/Registrar", Corpo("Evento", 10m, EventosTestKit.Hoje.AddDays(-1)))).StatusCode);
    }

    // ------------------------------------------------------------------ banco: triggers, constraints, índices, rowversion

    [Fact]
    public async Task Trigger_ComContexto_RegistraSnapshot_ApenasNaTransicaoDe1Para0_ETrataVariasLinhas()
    {
        var m = await MembrosAsync(2);
        var a = await CriarAsync(m[0]);
        var b = await CriarAsync(m[1]);
        await using var conexao = await ConexaoAsync();

        // alterações comuns e no-op: nenhum histórico, sem exigir contexto
        await ExecAsync(conexao, "UPDATE dbo.eventos SET [Local] = N'Novo local' WHERE Id = @id", ("@id", a.Id));
        await ExecAsync(conexao, "UPDATE dbo.eventos SET Ativo = Ativo WHERE Id IN (@a, @b)", ("@a", a.Id), ("@b", b.Id));
        Assert.Equal(0, await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == a.Id || h.EventoId == b.Id)));

        // sem contexto obrigatório: falha e nada muda (nem o evento, nem o histórico)
        Assert.Equal(50011, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.eventos SET Ativo = 0 WHERE Id IN (@a, @b)", ("@a", a.Id), ("@b", b.Id))));
        Assert.Equal(2, await Db(db => db.Eventos.CountAsync(e => (e.Id == a.Id || e.Id == b.Id) && e.Ativo)));

        // contexto parcial também falha
        await ExecAsync(conexao, "EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u", ("@u", fx.AdminId));
        Assert.Equal(50011, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.eventos SET Ativo = 0 WHERE Id = @a", ("@a", a.Id))));

        // com contexto completo: uma linha de histórico por evento desativado, numa única instrução
        await ExecAsync(conexao, """
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=N'2001:db8::1';
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=N'trigger direto';
            """);
        await ExecAsync(conexao, "UPDATE dbo.eventos SET Ativo = 0 WHERE Id IN (@a, @b)", ("@a", a.Id), ("@b", b.Id));
        var historico = await Db(db => db.HistoricoEventos.AsNoTracking().Where(h => h.EventoId == a.Id || h.EventoId == b.Id).ToListAsync());
        Assert.Equal(2, historico.Count);
        Assert.All(historico, h => Assert.Equal((fx.AdminId, "2001:db8::1", "trigger direto"), (h.UsuarioResponsavelId, h.IpResponsavel, h.Motivo)));
        Assert.Equal("Novo local", historico.Single(h => h.EventoId == a.Id).Local);  // snapshot dos dados imediatamente anteriores

        // segunda exclusão (0 -> 0) e reativação não geram novo histórico
        await ExecAsync(conexao, "UPDATE dbo.eventos SET Ativo = 0 WHERE Id IN (@a, @b)", ("@a", a.Id), ("@b", b.Id));
        Assert.Equal(2, await Db(db => db.HistoricoEventos.CountAsync(h => h.EventoId == a.Id || h.EventoId == b.Id)));
    }

    [Fact]
    public async Task HistoricoEventos_SnapshotNaoDependeDeLinhasMutaveis()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, "snapshot", dto.Versao)).StatusCode);

        await using var conexao = await ConexaoAsync();
        // altera as linhas mutáveis depois da exclusão: o histórico permanece igual
        await ExecAsync(conexao, "UPDATE dbo.eventos SET [Local] = N'mudou', SeguroObrigatorio = 0, ValorPorMembro = 18 WHERE Id = @id", ("@id", dto.Id));
        await ExecAsync(conexao, "UPDATE dbo.Lancamentos SET Valor = 1, Status = 1 WHERE EventoId = @id", ("@id", dto.Id));
        var h = await Db(db => db.HistoricoEventos.AsNoTracking().SingleAsync(x => x.EventoId == dto.Id));
        Assert.Equal(("Parque Ibirapuera", 20m, 2m, 40m), (h.Local, h.ValorPorMembro, h.SeguroObrigatorio, h.Total));
        Assert.All(JsonNode.Parse(h.ParticipantesJson)!.AsArray(), p => Assert.Equal(("Pendente", 20m), ((string)p!["status"]!, (decimal)p["valor"]!)));
    }

    [Fact]
    public async Task Indice_UnicoFiltrado_ImpedeMembroAtivoDuplicadoNoGrupo_MasPermiteReutilizarDepoisDeDesativar()
    {
        var m = await MembrosAsync(2);
        var g1 = await CriarAsync(m[0]);
        var g2 = await CriarAsync(m[1], ajustes: [("eventoReferenciaId", g1.Id)]);
        await using var conexao = await ConexaoAsync();

        // move a participação do g2 para o membro que já está ativo no g1 (mesmo grupo): violação de unicidade
        var erro = await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.evento_membros SET MembroId = @m WHERE EventoId = @e", ("@m", m[0]), ("@e", g2.Id)));
        Assert.Equal(2601, erro);

        // desativada a participação do g1, o índice filtrado libera o membro
        await ExecAsync(conexao, "UPDATE dbo.evento_membros SET Ativo = 0 WHERE EventoId = @e", ("@e", g1.Id));
        Assert.Equal(1, await ExecAsync(conexao, "UPDATE dbo.evento_membros SET MembroId = @m WHERE EventoId = @e", ("@m", m[0]), ("@e", g2.Id)));

        // e agora reativar a participação do g1 (membro já ativo no g2 do mesmo grupo) volta a falhar
        Assert.Equal(2601, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.evento_membros SET Ativo = 1 WHERE EventoId = @e", ("@e", g1.Id))));
    }

    [Fact]
    public async Task IndicesEConstraints_ExistemComAsPropriedadesEsperadas()
    {
        async Task<(bool Unico, string? Filtro)?> Indice(string tabela, string nome)
        {
            await using var conexao = await ConexaoAsync();
            await using var c = conexao.CreateCommand();
            c.CommandText = "SELECT is_unique, filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID(@t) AND name = @n";
            c.Parameters.AddWithValue("@t", tabela);
            c.Parameters.AddWithValue("@n", nome);
            await using var r = await c.ExecuteReaderAsync();
            return await r.ReadAsync() ? (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetString(1)) : null;
        }

        var filtrado = await Indice("dbo.evento_membros", "UX_evento_membros_grupo_membro_ativo");
        Assert.True(filtrado?.Unico);
        Assert.Contains("[Ativo]=(1)", filtrado!.Value.Filtro!.Replace(" ", ""));
        Assert.NotNull(await Indice("dbo.eventos", "IX_eventos_Ativo_DataEvento"));
        Assert.NotNull(await Indice("dbo.eventos", "IX_eventos_EventoGrupoId"));
        Assert.NotNull(await Indice("dbo.eventos", "IX_eventos_EventoReferenciaId"));
        Assert.NotNull(await Indice("dbo.Lancamentos", "IX_Lancamentos_EventoId"));
        Assert.NotNull(await Indice("dbo.evento_membros", "IX_evento_membros_EventoId_EventoGrupoId"));
        Assert.NotNull(await Indice("dbo.evento_membros", "IX_evento_membros_MembroId"));
        Assert.True((await Indice("dbo.eventos_operacoes", "IX_eventos_operacoes_UsuarioId_IdempotencyKey"))?.Unico);
        Assert.Equal("decimal", await EscalarAsync<string>("SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'eventos' AND COLUMN_NAME = 'ValorPorMembro'"));
        Assert.Equal((byte)18, await EscalarAsync<byte>("SELECT NUMERIC_PRECISION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'eventos' AND COLUMN_NAME = 'ValorPorMembro'"));
        Assert.Equal("timestamp", await EscalarAsync<string>("SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'eventos' AND COLUMN_NAME = 'Versao'"));
        Assert.Equal("YES", await EscalarAsync<string>("SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Lancamentos' AND COLUMN_NAME = 'Finalidade'"));
    }

    [Fact]
    public async Task Constraints_RejeitamValoresNegativosNaoNormalizadosEGrupoIncoerente()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await using var conexao = await ConexaoAsync();

        Assert.Equal(547, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.eventos SET TransporteValor = -1, ValorPorMembro = 9 WHERE Id = @id", ("@id", dto.Id))));
        Assert.Equal(547, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.eventos SET ValorPorMembro = 999 WHERE Id = @id", ("@id", dto.Id))));
        Assert.Equal(547, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.eventos SET TransporteEhGratis = 1 WHERE Id = @id", ("@id", dto.Id)))); // gratis com valor != 0
        Assert.Equal(547, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.eventos SET EventoGrupoId = NEWID() WHERE Id = @id", ("@id", dto.Id))));
        // FK composta: participante não pode declarar um grupo diferente do do evento
        Assert.Equal(547, await NumeroDoErroAsync(() => ExecAsync(conexao, "UPDATE dbo.evento_membros SET EventoGrupoId = NEWID() WHERE EventoId = @id", ("@id", dto.Id))));
        Assert.Equal(20m, (await ObterAsync(dto.Id)).ValorPorMembro);
    }

    [Fact]
    public async Task Rowversion_MudaEmTodoUpdate_EEhDevolvidaNoDto()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        var antes = await EscalarAsync<byte[]>("SELECT Versao FROM dbo.eventos WHERE Id = @id", ("@id", dto.Id));
        Assert.Equal(Convert.ToBase64String(antes!), dto.Versao);

        await using (var conexao = await ConexaoAsync())
            await ExecAsync(conexao, "UPDATE dbo.eventos SET AtualizadoEmUtc = SYSUTCDATETIME() WHERE Id = @id", ("@id", dto.Id));

        var depois = await EscalarAsync<byte[]>("SELECT Versao FROM dbo.eventos WHERE Id = @id", ("@id", dto.Id));
        Assert.NotEqual(antes, depois);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(Client, dto.Id, Editar(dto, m))).StatusCode);
    }

    [Fact]
    public async Task MigrationDeLancamentos_FinalidadeNulaEEventoIdNoHistoricoEAuditoriaDoTrigger()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(Client, dto.Id, "auditar lancamento", dto.Versao)).StatusCode);

        var aud = Assert.Single(await Db(db => db.LancamentosDeletados.AsNoTracking().Where(d => d.EventoId == dto.Id).ToListAsync()));
        Assert.Null(aud.Finalidade);
        Assert.Equal((CategoriaLancamento.Evento, StatusLancamento.Pendente, TipoFluxoLancamento.Entrada, 20m),
            (aud.Categoria, aud.Status, aud.TipoFluxo, aud.Valor));
        Assert.Equal(dto.Lancamentos[0].Id, aud.LancamentoId);
    }

    // ------------------------------------------------------------------ transação: rollback

    private sealed class FalhaAposSalvarInterceptor : SaveChangesInterceptor
    {
        public volatile bool Armada;

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armada) throw new InvalidOperationException("falha simulada depois de gravar, antes do commit");
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Transacao_FalhaDepoisDeGravar_DesfazEventoParticipantesLancamentosEIdempotencia_ERetryFunciona()
    {
        var interceptor = new FalhaAposSalvarInterceptor();
        using var factory = new SqlServerApiFactory(configureDb: o => o.AddInterceptors(interceptor));
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var m = new List<Guid> { (await TestHelpers.AddUsuarioAsync(factory, "Rollback 1", "DS")).Id, (await TestHelpers.AddUsuarioAsync(factory, "Rollback 2", "DS")).Id };
        var chave = Guid.NewGuid().ToString();

        interceptor.Armada = true;
        var falha = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(m), chave);
        interceptor.Armada = false;

        Assert.Equal(HttpStatusCode.InternalServerError, falha.StatusCode);
        Assert.Equal(0, await TestHelpers.WithDbAsync(factory, db => db.Eventos.CountAsync()));
        Assert.Equal(0, await TestHelpers.WithDbAsync(factory, db => db.EventosMembros.CountAsync()));
        Assert.Equal(0, await TestHelpers.WithDbAsync(factory, db => db.EventosOperacoes.CountAsync()));
        Assert.Equal(0, await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync(l => l.EventoId != null)));

        // retry com a mesma chave depois da falha cria normalmente, sem duplicar
        var retry = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(m), chave);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(2, await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync(l => l.EventoId != null)));
        var replay = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(m), chave);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(2, await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync(l => l.EventoId != null)));
    }

    [Fact]
    public async Task Transacao_FalhaNoPut_NaoDeixaAlteracaoParcial_NemContextoDeAuditoria()
    {
        var interceptor = new FalhaAposSalvarInterceptor();
        using var factory = new SqlServerApiFactory(configureDb: o => o.AddInterceptors(interceptor));
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var m = new List<Guid> { (await TestHelpers.AddUsuarioAsync(factory, "Put 1", "DS")).Id, (await TestHelpers.AddUsuarioAsync(factory, "Put 2", "DS")).Id };
        var criado = await EventosTestKit.LerAsync(await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(m)));

        interceptor.Armada = true;
        var falha = await EventosTestKit.PutAsync(client, criado.Id, Editar(criado, m[0], transporte: 50m, motivo: "remoção que falha"));
        interceptor.Armada = false;

        Assert.Equal(HttpStatusCode.InternalServerError, falha.StatusCode);
        var lancamentos = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking().Where(l => l.EventoId == criado.Id).ToListAsync());
        Assert.All(lancamentos, l => Assert.Equal((20m, true), (l.Valor, l.Ativo)));
        Assert.Equal(2, await TestHelpers.WithDbAsync(factory, db => db.EventosMembros.CountAsync(p => p.EventoId == criado.Id && p.Ativo)));
        Assert.Equal(0, await TestHelpers.WithDbAsync(factory, db => db.LancamentosDeletados.CountAsync()));
        var atual = await EventosTestKit.LerAsync(await client.GetAsync($"/api/Eventos/{criado.Id}"));
        Assert.Equal(criado.Versao, atual.Versao); // até o "lock" de versão foi revertido

        // depois da falha, o mesmo PUT (mesma versão) funciona e o contexto de auditoria não ficou pendurado
        var ok = await EventosTestKit.PutAsync(client, criado.Id, Editar(criado, m[0], transporte: 50m, motivo: "remoção"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, await TestHelpers.WithDbAsync(factory, db => db.LancamentosDeletados.CountAsync(d => d.Motivo == "remoção")));
    }
}
