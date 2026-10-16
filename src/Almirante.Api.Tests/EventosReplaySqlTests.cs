using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Tests;

// Replay idempotente de POST /api/Eventos: devolve o resultado ORIGINAL (200), não o estado atual, com a versão
// original; independe do relógio (virada de mês); nunca vaza entre usuários; e a resposta original é gravada na MESMA
// transação do evento. SQL Server real, relógio controlável e estratégia de retry equivalente à do Aspire.
[Trait("Category", "RequiresSqlServer")]
public sealed class EventosReplaySqlTests : IAsyncLifetime
{
    // Falha ao persistir a resposta original: dispara quando a gravação carrega um EventoOperacao alterado (o UPDATE
    // que acrescenta a resposta depois de o banco gerar a rowversion), i.e. ENTRE gravar o evento e gravar a resposta.
    private sealed class FalhaAoPersistirRespostaInterceptor : SaveChangesInterceptor
    {
        public volatile bool Armada;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armada && eventData.Context!.ChangeTracker.Entries<EventoOperacao>().Any(e => e.State == EntityState.Modified))
                throw new InvalidOperationException("falha simulada entre gravar o evento e gravar a resposta original");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private readonly FalhaAoPersistirRespostaInterceptor _falha = new();
    private readonly MutableTimeProvider _clock = new();
    private SqlServerApiFactory _factory = null!;
    private HttpClient _client = null!;
    private HttpClient _outroUsuario = null!;
    private Guid _outroUsuarioId;
    private HttpClient _semPermissao = null!;

    // Datas fixas em relação ao relógio CONTROLADO (não ao relógio real).
    private static readonly DateTimeOffset UltimoDiaDeAgosto = new(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PrimeiroDiaDeSetembro = new(2026, 9, 1, 0, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly DataDeAgosto = new(2026, 8, 31);

    public async Task InitializeAsync()
    {
        // Só o EventosService usa o relógio controlado (a autenticação segue o relógio real: o JWT e as sessões dependem dele).
        _factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(_falha), s => s.AddScoped(sp => new EventosService(
            sp.GetRequiredService<AlmiranteDbContext>(), _clock, sp.GetRequiredService<ILogger<EventosService>>(), sp.GetRequiredService<EventosLockOptions>())));
        _client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
        (_outroUsuario, _outroUsuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, "TES");
        (_semPermissao, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, "DS");
        _clock.Now = UltimoDiaDeAgosto;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _outroUsuario.Dispose();
        _semPermissao.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<List<Guid>> Membros(int n) => EventosSqlSupport.MembrosAsync(_factory, n);
    private Task<T?> Sql<T>(string sql, params (string, object)[] p) => EventosSqlSupport.EscalarAsync<T>(_factory, sql, p);
    private static string Nova() => Guid.NewGuid().ToString();

    private Task<HttpResponseMessage> Post(HttpClient client, object? membros, string chave, string local = "Parque Ibirapuera", decimal transporte = 10m,
        bool gratis = false, decimal alimentacao = 8m, bool individual = false, decimal seguro = 2m) =>
        EventosTestKit.PostAsync(client, EventosTestKit.Corpo(membros, DataDeAgosto, local, transporte, gratis, alimentacao, individual, seguro), chave);

    private async Task<EventoDto> Criar(object? membros, string chave, HttpClient? client = null, string local = "Parque Ibirapuera")
    {
        var response = await Post(client ?? _client, membros, chave, local);
        Assert.True(HttpStatusCode.Created == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await EventosTestKit.LerAsync(response);
    }

    private async Task<EventoDto> Obter(Guid id) => (await _client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{id}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }))!;

    private Task<int> ContarEventos() => Sql<int>("SELECT COUNT(*) FROM eventos");
    private Task<int> ContarOperacoes() => Sql<int>("SELECT COUNT(*) FROM eventos_operacoes");
    private Task<int> ContarLancamentosDeEvento() => Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE EventoId IS NOT NULL");

    // ------------------------------------------------------------------ resultado original ao longo do tempo

    [Fact]
    public async Task CriarEditarCustosEParticipantes_ReplayDevolveOResultadoOriginal_ComAVersaoOriginal_SemNovosRegistros()
    {
        var m = await Membros(2);
        var chave = Nova();
        var original = await Criar(m, chave);
        var put = await EventosTestKit.PutAsync(_client, original.Id, EventosSqlSupport.Editar(original, m[0], transporte: 50m, motivo: "saiu"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var atual = await Obter(original.Id);
        Assert.NotEqual(original.Versao, atual.Versao);
        var (eventos, operacoes, lancamentos) = (await ContarEventos(), await ContarOperacoes(), await ContarLancamentosDeEvento());

        var replay = await Post(_client, m, chave);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await EventosTestKit.LerAsync(replay), EventoDtoComparer.Instance);  // mesmo ID e DTO original
        Assert.Equal(original.Versao, (await EventosTestKit.LerAsync(replay)).Versao);
        Assert.Equal((eventos, operacoes, lancamentos), (await ContarEventos(), await ContarOperacoes(), await ContarLancamentosDeEvento()));
        // o GET continua sendo o estado atual: 1 participante, valor novo
        Assert.Equal((1, 60m), (atual.QuantidadeMembros, atual.ValorPorMembro));
        Assert.Equal([m[0]], atual.Membros);
    }

    [Fact]
    public async Task ReplayNaoCombinaConteudoAntigoComVersaoAtual_EPutComAVersaoOriginalContinuaRecebendo409()
    {
        var m = await Membros(2);
        var chave = Nova();
        var original = await Criar(m, chave);
        Assert.Equal(HttpStatusCode.OK, (await EventosTestKit.PutAsync(_client, original.Id, EventosSqlSupport.Editar(original, m, transporte: 30m))).StatusCode);
        var atual = await Obter(original.Id);

        var reenvio = await EventosTestKit.LerAsync(await Post(_client, m, chave));

        Assert.Equal(original.Versao, reenvio.Versao);
        Assert.NotEqual(atual.Versao, reenvio.Versao);
        Assert.Equal(20m, reenvio.ValorPorMembro);  // conteúdo original (antigo)
        // editar a partir do replay (dados antigos + versão original) não passa pelo controle de concorrência
        var put = await EventosTestKit.PutAsync(_client, original.Id, EventosSqlSupport.Editar(reenvio, m, transporte: 99m));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(atual.Transporte, (await Obter(original.Id)).Transporte);
    }

    [Fact]
    public async Task CriarPagarERepetir_PreservaOResultadoOriginal_EOGetMostraOPagamentoAtual()
    {
        var m = await Membros(1);
        var chave = Nova();
        var original = await Criar(m, chave);
        Assert.Equal(HttpStatusCode.OK, (await EventosSqlSupport.PagarAsync(_client, original.Lancamentos[0].Id)).StatusCode);

        var replay = await EventosTestKit.LerAsync(await Post(_client, m, chave));

        Assert.Equal(StatusLancamento.Pendente, Assert.Single(replay.Lancamentos).Status);  // resultado original prometido
        Assert.Equal(original.Versao, replay.Versao);
        var atual = await Obter(original.Id);
        Assert.Equal(StatusLancamento.Pago, Assert.Single(atual.Lancamentos).Status);
        Assert.NotEqual(original.Versao, atual.Versao);
    }

    [Fact]
    public async Task RespostaOriginalEGravadaComAVersaoGeradaPeloBanco_ESemDadosSensiveis()
    {
        var m = await Membros(2);
        var chave = Nova();
        var original = await Criar(m, chave);

        var json = await Sql<string>("SELECT RespostaJson FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave));
        Assert.NotNull(json);
        var no = JsonNode.Parse(json!)!.AsObject();
        Assert.Equal(original.Versao, (string)no["versao"]!);
        Assert.Equal(Convert.ToBase64String((await Sql<byte[]>("SELECT Versao FROM eventos WHERE Id = @e", ("@e", original.Id)))!), (string)no["versao"]!);
        // só os campos do DTO de evento: nada de token, cookie, connection string, claims ou entidade de usuário
        var esperados = new[] { "id", "eventoGrupoId", "eventoReferenciaId", "dataEvento", "local", "transporte", "alimentacao", "seguroObrigatorio",
            "membros", "quantidadeMembros", "valorPorMembro", "total", "ativo", "versao", "lancamentos" };
        Assert.Empty(no.Select(p => p.Key).Except(esperados, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("senha", json!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Server=", json!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ relógio: virada de mês

    [Fact]
    public async Task CriarEm30OuUltimoDiaDoMes_ReenviarNoMesSeguinte_EReplayValido_MasChaveNovaComDataAntigaEh400()
    {
        var m = await Membros(1);
        var chave = Nova();
        var original = await Criar(m, chave);

        _clock.Now = PrimeiroDiaDeSetembro;
        var replay = await Post(_client, m, chave);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original.Id, (await EventosTestKit.LerAsync(replay)).Id);

        // operação nova (chave nova) com a data agora anterior ao mês corrente continua inválida
        var nova = await Post(_client, (await Membros(1))[0], Nova());
        Assert.Equal(HttpStatusCode.BadRequest, nova.StatusCode);
        Assert.Contains("DataEvento", await nova.Content.ReadAsStringAsync());
        Assert.Equal(1, await ContarEventos());
    }

    [Fact]
    public async Task MesmaChaveComConteudoDiferente_Retorna409_InclusiveDepoisDaViradaDoMes_ComCorpoBemFormado()
    {
        var m = await Membros(1);
        var chave = Nova();
        await Criar(m, chave);

        _clock.Now = PrimeiroDiaDeSetembro;
        var diferente = await Post(_client, m, chave, local: "Outro Parque");

        Assert.Equal(HttpStatusCode.Conflict, diferente.StatusCode);  // conflito de idempotência, não 400 de data nem de parsing
        Assert.Contains("dados diferentes", await diferente.Content.ReadAsStringAsync());
        Assert.Equal(1, await ContarEventos());
    }

    [Fact]
    public async Task JsonMalformadoComChaveExistente_Continua400_ERelogioNaoAutorizaIgnorarAIdentidade()
    {
        var m = await Membros(1);
        var chave = Nova();
        await Criar(m, chave);

        var malformado = await EventosTestKit.PostJsonBrutoAsync(_client, "{ \"local\": ", chave);
        Assert.Equal(HttpStatusCode.BadRequest, malformado.StatusCode);
        var semMembros = await EventosTestKit.PostJsonBrutoAsync(_client, "{}", chave);   // campos obrigatórios ausentes: 400 de formato
        Assert.Equal(HttpStatusCode.BadRequest, semMembros.StatusCode);

        // sem autenticação e sem permissão, uma chave existente não devolve nada
        using var anonimo = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonimo, m, chave)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(_semPermissao, m, chave)).StatusCode);
    }

    // ------------------------------------------------------------------ equivalência

    [Fact]
    public async Task ReplayEquivalente_MembrosReordenados_GuidUnicoVersusArray_ENumerosIgnorados_DevolveOOriginalMesmoDepoisDeEditar()
    {
        var m = await Membros(2);

        // (a) ordem dos membros
        var c1 = Nova();
        var a = await Criar(new[] { m[0], m[1] }, c1);
        Assert.Equal(HttpStatusCode.OK, (await EventosTestKit.PutAsync(_client, a.Id, EventosSqlSupport.Editar(a, m, transporte: 12m))).StatusCode);
        var ra = await Post(_client, new[] { m[1], m[0] }, c1);
        Assert.Equal((HttpStatusCode.OK, a.Versao, 20m), (ra.StatusCode, (await EventosTestKit.LerAsync(ra)).Versao, (await EventosTestKit.LerAsync(ra)).ValorPorMembro));

        // (b) GUID único versus array de um elemento
        var solo = (await Membros(1))[0];
        var c2 = Nova();
        var b = await Criar(solo, c2);
        var rb = await Post(_client, new[] { solo }, c2);
        Assert.Equal((HttpStatusCode.OK, b.Id), (rb.StatusCode, (await EventosTestKit.LerAsync(rb)).Id));

        // (c) valores numéricos ignorados pelos booleanos (transporte grátis / alimentação individual)
        var outro = (await Membros(1))[0];
        var c3 = Nova();
        var r1 = await Post(_client, outro, c3, transporte: 555m, gratis: true, alimentacao: 777m, individual: true);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var c = await EventosTestKit.LerAsync(r1);
        var rc = await Post(_client, outro, c3, transporte: 1m, gratis: true, alimentacao: 2m, individual: true);
        Assert.Equal((HttpStatusCode.OK, c.Id, 2m), (rc.StatusCode, (await EventosTestKit.LerAsync(rc)).Id, (await EventosTestKit.LerAsync(rc)).ValorPorMembro));
    }

    // ------------------------------------------------------------------ usuários e exclusão

    [Fact]
    public async Task MesmaChaveParaUsuariosDistintos_IsolaOReplay_SemVazarRespostaAlheia()
    {
        var m = await Membros(1);
        var chave = Nova();
        var doAdmin = await Criar(m, chave);
        Assert.Equal(HttpStatusCode.OK, (await EventosTestKit.PutAsync(_client, doAdmin.Id, EventosSqlSupport.Editar(doAdmin, m, transporte: 40m))).StatusCode);

        // outro usuário, mesma chave e mesmo conteúdo: cria o PRÓPRIO evento (grupo diferente), nunca recebe o do primeiro
        var outroMembro = (await Membros(1))[0];
        var doOutro = await Criar(outroMembro, chave, _outroUsuario);
        Assert.NotEqual(doAdmin.Id, doOutro.Id);
        Assert.Equal(2, await Sql<int>("SELECT COUNT(*) FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave)));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM eventos_operacoes WHERE IdempotencyKey = @k AND UsuarioId = @u", ("@k", chave), ("@u", _outroUsuarioId)));

        // e o replay de cada um devolve o seu
        var replayOutro = await EventosTestKit.LerAsync(await Post(_outroUsuario, outroMembro, chave));
        Assert.Equal(doOutro.Id, replayOutro.Id);
        var replayAdmin = await EventosTestKit.LerAsync(await Post(_client, m, chave));
        Assert.Equal((doAdmin.Id, 20m), (replayAdmin.Id, replayAdmin.ValorPorMembro));
    }

    [Fact]
    public async Task ExcluirERepetirOPost_Retorna409_SemReativarNemDuplicarHistorico_MesmoComRespostaSalva()
    {
        var m = await Membros(1);
        var chave = Nova();
        var original = await Criar(m, chave);
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(_client, original.Id, "excluído", original.Versao)).StatusCode);

        var replay = await Post(_client, m, chave);

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(0, await Sql<int>("SELECT CAST(Ativo AS int) FROM eventos WHERE Id = @e", ("@e", original.Id)));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM historico_eventos WHERE EventoId = @e", ("@e", original.Id)));
        Assert.Equal((1, 1, 1), (await ContarEventos(), await ContarOperacoes(), await ContarLancamentosDeEvento()));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE EventoId = @e AND Ativo = 1", ("@e", original.Id)));
    }

    // ------------------------------------------------------------------ concorrência e atomicidade

    [Fact]
    public async Task DuasRequisicoesEquivalentesConcorrentes_UmEventoUmaOperacaoEUmConjuntoDeLancamentos_RespostaOriginalConsistente()
    {
        for (var rodada = 0; rodada < 4; rodada++)
        {
            var m = await Membros(2);
            var chave = Nova();
            var respostas = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Post(_client, m, chave)));

            Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.All(respostas, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
            var dtos = await Task.WhenAll(respostas.Select(EventosTestKit.LerAsync));
            Assert.Single(dtos.Select(d => d.Id).Distinct());
            Assert.Single(dtos.Select(d => d.Versao).Distinct());
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave)));
            Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM eventos WHERE Id = @e", ("@e", dtos[0].Id)));
            Assert.Equal(2, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE EventoId = @e", ("@e", dtos[0].Id)));
            Assert.NotNull(await Sql<string>("SELECT RespostaJson FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave)));
        }
    }

    [Fact]
    public async Task FalhaEntreGravarOEventoEGravarARespostaOriginal_ReverteTudo_ENovaTentativaEhSegura()
    {
        var m = await Membros(2);
        var chave = Nova();

        _falha.Armada = true;
        var falha = await Post(_client, m, chave);
        _falha.Armada = false;

        Assert.Equal(HttpStatusCode.InternalServerError, falha.StatusCode);
        Assert.Equal((0, 0, 0), (await ContarEventos(), await ContarOperacoes(), await ContarLancamentosDeEvento()));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM evento_membros"));

        var nova = await Post(_client, m, chave);
        Assert.Equal(HttpStatusCode.Created, nova.StatusCode);
        Assert.NotNull(await Sql<string>("SELECT RespostaJson FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave)));
        Assert.Equal((1, 1, 2), (await ContarEventos(), await ContarOperacoes(), await ContarLancamentosDeEvento()));
    }

    // ------------------------------------------------------------------ operações anteriores à correção

    [Fact]
    public async Task OperacaoPreexistenteSemRespostaSalva_Retorna409EstavelSemCriarNada_EPreservaAChave()
    {
        var m = await Membros(1);
        var chave = Nova();
        var original = await Criar(m, chave);
        // simula uma operação criada ANTES da correção: não existe resposta original persistida
        await using (var c = new Microsoft.Data.SqlClient.SqlConnection(_factory.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE eventos_operacoes SET RespostaJson = NULL WHERE IdempotencyKey = @k";
            cmd.Parameters.AddWithValue("@k", chave);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        var replay = await Post(_client, m, chave);

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var problema = JsonNode.Parse(await replay.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("EVENTO_IDEMPOTENCIA_SEM_RESPOSTA_ORIGINAL", (string)problema["codigo"]!);
        Assert.Equal(original.Id.ToString(), (string)problema["eventoId"]!);
        Assert.Contains("Consulte", (string)problema["detail"]!);
        Assert.DoesNotContain("nova Idempotency-Key", (string)problema["detail"]!, StringComparison.OrdinalIgnoreCase);  // sem recomendar recriação
        Assert.Equal((1, 1, 1), (await ContarEventos(), await ContarOperacoes(), await ContarLancamentosDeEvento()));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM eventos_operacoes WHERE IdempotencyKey = @k", ("@k", chave)));   // chave não apagada

        // conteúdo diferente com a mesma chave continua sendo o conflito de idempotência
        var diferente = await Post(_client, m, chave, local: "Outro Parque");
        Assert.Equal(HttpStatusCode.Conflict, diferente.StatusCode);
        Assert.Contains("dados diferentes", await diferente.Content.ReadAsStringAsync());
    }
}

// Comparação estrutural de EventoDto (listas por valor).
internal sealed class EventoDtoComparer : IEqualityComparer<EventoDto>
{
    public static readonly EventoDtoComparer Instance = new();

    public bool Equals(EventoDto? x, EventoDto? y) => x is not null && y is not null
        && (x.Id, x.EventoGrupoId, x.EventoReferenciaId, x.DataEvento, x.Local, x.Transporte, x.Alimentacao, x.SeguroObrigatorio, x.QuantidadeMembros,
            x.ValorPorMembro, x.Total, x.Ativo, x.Versao)
        == (y.Id, y.EventoGrupoId, y.EventoReferenciaId, y.DataEvento, y.Local, y.Transporte, y.Alimentacao, y.SeguroObrigatorio, y.QuantidadeMembros,
            y.ValorPorMembro, y.Total, y.Ativo, y.Versao)
        && x.Membros.SequenceEqual(y.Membros) && x.Lancamentos.SequenceEqual(y.Lancamentos);

    public int GetHashCode(EventoDto obj) => obj.Id.GetHashCode();
}
