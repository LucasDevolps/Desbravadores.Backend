using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentoPagamentoResilienciaSqlTests : IAsyncLifetime
{
    private readonly FalhaDeCommitInterceptor _falha = new();
    private readonly PausaDeComandoInterceptor _pausa = new();
    private readonly FalhasInjetaveisNoBanco _falhas = new();
    private SqlServerApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(_falha, _pausa, _falhas));
        _client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static bool TocaEventoParaPagamento(string sql) =>
        sql.Contains("UPDATE dbo.eventos SET AtualizadoEmUtc") && !sql.Contains("AtualizadoPorUsuarioId");

    private Task<T?> Sql<T>(string sql, params (string, object)[] p) => EventosSqlSupport.EscalarAsync<T>(_factory, sql, p);

    private async Task<(EventoDto Evento, List<Guid> Membros)> EventoAsync(int membros)
    {
        var m = await EventosSqlSupport.MembrosAsync(_factory, membros);
        return (await EventosSqlSupport.CriarAsync(_client, m), m);
    }

    private static async Task<LancamentoDto> LerLancamentoAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<LancamentoDto>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }))!;

    [Fact]
    public async Task FalhaTransitoriaAntesDoCommit_RetentativaPersisteOStatus_ERespostaCoincideComOBanco()
    {
        var (evento, _) = await EventoAsync(2);
        var alvo = evento.Lancamentos[0].Id;
        var versaoAntes = await Sql<byte[]>("SELECT Versao FROM eventos WHERE Id = @e", ("@e", evento.Id));

        _falha.Armar(MomentoDaFalha.AntesDoCommit);
        var response = await EventosSqlSupport.PagarAsync(_client, alvo);
        _falha.Desarmar();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StatusLancamento.Pago, (await LerLancamentoAsync(response)).Status);
        Assert.Equal(2, _falha.TransacoesIniciadas);
        Assert.Equal(1, _falha.CommitsEfetivados);
        Assert.Equal(1, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.NotNull(await Sql<DateTime?>("SELECT DataAtualizacao FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.NotEqual(versaoAntes, await Sql<byte[]>("SELECT Versao FROM eventos WHERE Id = @e", ("@e", evento.Id)));
        Assert.Equal(0, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", evento.Lancamentos[1].Id)));
    }

    [Fact]
    public async Task CommitEfetivadoComConfirmacaoPerdida_NaoRepeteAOperacao_ERespondeComOEstadoReal()
    {
        var (evento, _) = await EventoAsync(2);
        var alvo = evento.Lancamentos[0].Id;

        _falha.Armar(MomentoDaFalha.DepoisDoCommit);
        var response = await EventosSqlSupport.PagarAsync(_client, alvo);
        _falha.Desarmar();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await LerLancamentoAsync(response);
        Assert.Equal(StatusLancamento.Pago, dto.Status);
        Assert.Equal(evento.Lancamentos[0].Valor, dto.Valor);
        Assert.Equal(1, _falha.TransacoesIniciadas);
        Assert.Equal(1, _falha.CommitsEfetivados);
        Assert.Equal(1, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));

        var repetido = await EventosSqlSupport.PagarAsync(_client, alvo);
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal(1, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
    }

    [Fact]
    public async Task FalhasRepetidasAlemDoLimiteDeRetries_NaoDevolvemSucesso_ENaoPersistem()
    {
        var (evento, _) = await EventoAsync(1);
        var alvo = evento.Lancamentos[0].Id;

        _falha.Armar(MomentoDaFalha.AntesDoCommit, vezes: 10);
        var response = await EventosSqlSupport.PagarAsync(_client, alvo);
        _falha.Desarmar();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.Equal(0, _falha.CommitsEfetivados);
    }

    [Fact]
    public async Task PagamentoLidoAntesDaExclusaoDoEvento_NaoGravaStatus_Retorna404()
    {
        var (evento, _) = await EventoAsync(2);
        var alvo = evento.Lancamentos[0].Id;

        var pausa = _pausa.Armar(TocaEventoParaPagamento);
        var pagar = EventosSqlSupport.PagarAsync(_client, alvo);
        await pausa.Atingida;
        var excluir = await EventosTestKit.DeleteAsync(_client, evento.Id, "exclusão no intervalo", evento.Versao);
        Assert.Equal(HttpStatusCode.NoContent, excluir.StatusCode);
        pausa.Liberar();

        Assert.Equal(HttpStatusCode.NotFound, (await pagar).StatusCode);
        Assert.Equal(0, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.Equal(0, await Sql<int>("SELECT CAST(Ativo AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
    }

    [Fact]
    public async Task PagamentoLidoAntesDoPutQueRemoveOParticipante_NaoMarcaComoPagoLancamentoInativo()
    {
        var (evento, membros) = await EventoAsync(2);
        var removido = evento.Lancamentos.Single(l => l.MembroId == membros[1]).Id;

        var pausa = _pausa.Armar(TocaEventoParaPagamento);
        var pagar = EventosSqlSupport.PagarAsync(_client, removido);
        await pausa.Atingida;
        var put = await EventosTestKit.PutAsync(_client, evento.Id, EventosSqlSupport.Editar(evento, membros[0], motivo: "saiu do passeio"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        pausa.Liberar();

        Assert.Equal(HttpStatusCode.NotFound, (await pagar).StatusCode);
        Assert.Equal(0, await Sql<int>("SELECT CAST(Ativo AS int) FROM Lancamentos WHERE Id = @l", ("@l", removido)));
        Assert.Equal(0, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", removido)));
    }

    /// <summary>A exclusão auditada também funciona sob estratégia de retentativa, e repeti-la responde 404 sem gerar outra cópia.</summary>
    [Fact]
    public async Task Exclusao_ComEstrategiaDeRetentativa_ExcluiEGravaOHistorico()
    {
        var membro = (await EventosSqlSupport.MembrosAsync(_factory, 1))[0];
        var criado = await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro);
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var id = (await criado.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        var exclusao = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{id}")
        {
            Content = JsonContent.Create(new { motivo = "lançado em duplicidade" }),
        });

        Assert.Equal(HttpStatusCode.NoContent, exclusao.StatusCode);
        Assert.Equal(0, await Sql<int>("SELECT CAST(Ativo AS int) FROM Lancamentos WHERE Id = @l", ("@l", id)));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM dbo.lancamentos_deletados WHERE LancamentoId = @l", ("@l", id)));

        var repetida = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{id}")
        {
            Content = JsonContent.Create(new { motivo = "lançado em duplicidade" }),
        });
        Assert.Equal(HttpStatusCode.NotFound, repetida.StatusCode);
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM dbo.lancamentos_deletados WHERE LancamentoId = @l", ("@l", id)));
    }

    private static Exception ViolacaoDeUnicidade() => new DbUpdateException("simulada", SqlExceptionDeTeste.Criar(2601));

    private static async Task<Guid> IdAsync(HttpResponseMessage resposta) =>
        (await resposta.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task Registro_FalhaPermanenteNaGravacao_SemChaveOuEmLote_Responde500SemGravar()
    {
        var membro = (await EventosSqlSupport.MembrosAsync(_factory, 1))[0];

        _falhas.FalharProximoSave(() => new DbUpdateException("recusado pelo banco (simulado)"));
        Assert.Equal(HttpStatusCode.InternalServerError, (await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro)).StatusCode);

        _falhas.FalharProximoSave(() => new DbUpdateException("recusado pelo banco (simulado)"));
        using var lote = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar")
        {
            Content = JsonContent.Create(new
            {
                finalidade = "Mensalidade", descricao = "Lote", categoria = "Clube", tipoFluxo = "Entrada", valor = 10m,
                vencimento = NotificacoesTestKit.Vencimento, aplicarATodosOsMembros = true,
            }),
        };
        lote.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.InternalServerError, (await _client.SendAsync(lote)).StatusCode);

        // O seed já tem um lançamento de exemplo: nada novo para o membro, e nenhuma operação de lote.
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE MembroId = @m", ("@m", membro)));
        Assert.Equal(0, await Sql<int>("SELECT COUNT(*) FROM LancamentosOperacoes"));
    }

    [Fact]
    public async Task Registro_MesmaChaveDepoisDaExclusao_409_ECorridaPelaMesmaChaveComOsMesmosDados_ReutilizaOVencedor()
    {
        var membro = (await EventosSqlSupport.MembrosAsync(_factory, 1))[0];
        var chave = Guid.NewGuid().ToString();
        var criado = await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro, chave: chave);
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{await IdAsync(criado)}")
        {
            Content = JsonContent.Create(new { motivo = "lançado em duplicidade" }),
        })).StatusCode);
        var repetido = await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro, chave: chave);
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Contains("Lançamento da operação já foi excluído.", await repetido.Content.ReadAsStringAsync());

        var outraChave = Guid.NewGuid().ToString();
        HttpResponseMessage? vencedor = null;
        _falhas.FalharProximoSave(ViolacaoDeUnicidade, async () => vencedor = await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro, chave: outraChave));
        var perdedor = await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro, chave: outraChave);

        Assert.Equal(HttpStatusCode.Created, vencedor!.StatusCode);
        Assert.Equal(HttpStatusCode.OK, perdedor.StatusCode);
        Assert.Equal(await IdAsync(vencedor), await IdAsync(perdedor));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE Ativo = 1 AND MembroId = @m", ("@m", membro)));
    }

    [Fact]
    public async Task Registro_ComChave_CommitComConfirmacaoPerdida_RetentativaReconheceAPropriaOperacao()
    {
        var membro = (await EventosSqlSupport.MembrosAsync(_factory, 1))[0];

        _falha.Armar(MomentoDaFalha.DepoisDoCommit);
        var resposta = await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro, chave: Guid.NewGuid().ToString());
        _falha.Desarmar();

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM Lancamentos WHERE MembroId = @m", ("@m", membro)));
        Assert.Equal(1, await Sql<int>("SELECT COUNT(*) FROM LancamentosOperacoes"));
    }

    [Fact]
    public async Task Listagem_PorVencimento_EAlteracaoDeFinalidadeEVencimento()
    {
        var membro = (await EventosSqlSupport.MembrosAsync(_factory, 1))[0];
        var id = await IdAsync(await NotificacoesTestKit.RegistrarDebitoAsync(_client, membro));
        var novoVencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(40).ToString("yyyy-MM-dd");

        var alterado = await _client.PutAsJsonAsync($"/api/Lancamentos/{id}", new { finalidade = "Uniflash", vencimento = novoVencimento });
        Assert.Equal(HttpStatusCode.OK, alterado.StatusCode);
        Assert.Equal("Uniflash", await Sql<string>("SELECT Finalidade FROM Lancamentos WHERE Id = @l", ("@l", id)));

        var lista = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/Lancamentos?vencimento={novoVencimento}");
        Assert.Equal(id, Assert.Single(lista.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var vazia = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/Lancamentos?vencimento={NotificacoesTestKit.Vencimento}");
        Assert.Empty(vazia.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PagamentoLidoAntesDeAlteracaoDeCustos_NaoUsaDadosObsoletos_EFicaCoerenteComOCadastro()
    {
        var (evento, membros) = await EventoAsync(1);
        var alvo = evento.Lancamentos[0].Id;

        var pausa = _pausa.Armar(TocaEventoParaPagamento);
        var pagar = EventosSqlSupport.PagarAsync(_client, alvo);
        await pausa.Atingida;
        var put = await EventosTestKit.PutAsync(_client, evento.Id, EventosSqlSupport.Editar(evento, membros, transporte: 77m));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        pausa.Liberar();

        var resposta = await pagar;
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var dto = await LerLancamentoAsync(resposta);
        Assert.Equal(StatusLancamento.Pago, dto.Status);
        Assert.Equal(87m, dto.Valor);
        Assert.Equal(87m, await Sql<decimal>("SELECT Valor FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.Equal(87m, await Sql<decimal>("SELECT ValorPorMembro FROM eventos WHERE Id = @e", ("@e", evento.Id)));
        Assert.Equal(1, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
    }
}
