using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Mudança de status (pagamento) de lançamento de evento sob a execution strategy com retry (equivalente à do Aspire)
// e sob intercalamentos forçados com PUT/DELETE do cadastro. Sempre SQL Server real; o estado persistido é lido
// por uma conexão independente da API, e comparado com o que a resposta HTTP afirmou.
[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentoPagamentoResilienciaSqlTests : IAsyncLifetime
{
    private readonly FalhaDeCommitInterceptor _falha = new();
    private readonly PausaDeComandoInterceptor _pausa = new();
    private SqlServerApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(_falha, _pausa));
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
        Assert.Equal(2, _falha.TransacoesIniciadas);   // a primeira tentativa foi revertida e a estratégia retentou
        Assert.Equal(1, _falha.CommitsEfetivados);
        // o que a resposta afirmou é o que está no banco, lido por outra conexão
        Assert.Equal(1, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.NotNull(await Sql<DateTime?>("SELECT DataAtualizacao FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.NotEqual(versaoAntes, await Sql<byte[]>("SELECT Versao FROM eventos WHERE Id = @e", ("@e", evento.Id)));
        // o outro lançamento não foi tocado
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
        Assert.Equal(evento.Lancamentos[0].Valor, dto.Valor); // dados reais lidos do banco, não fictícios
        Assert.Equal(1, _falha.TransacoesIniciadas);          // nenhuma nova tentativa da operação: o commit foi verificado
        Assert.Equal(1, _falha.CommitsEfetivados);
        Assert.Equal(1, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));

        // e o cadastro segue coerente: um segundo pagamento idempotente do mesmo status não regride nada
        var repetido = await EventosSqlSupport.PagarAsync(_client, alvo);
        Assert.Equal(HttpStatusCode.OK, repetido.StatusCode);
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

        // o pagamento já leu o lançamento (ativo) e para ANTES de travar o evento; a exclusão confirma nesse intervalo
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

    [Fact]
    public async Task PagamentoLidoAntesDeAlteracaoDeCustos_NaoUsaDadosObsoletos_EFicaCoerenteComOCadastro()
    {
        var (evento, membros) = await EventoAsync(1);
        var alvo = evento.Lancamentos[0].Id;

        // o pagamento leu o lançamento com valor 20 e para antes de travar o evento; o PUT confirma os novos custos (87)
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
        // a resposta reflete o valor confirmado pelo PUT, e o banco tem exatamente isso (evento e lançamento coerentes)
        Assert.Equal(87m, dto.Valor);
        Assert.Equal(87m, await Sql<decimal>("SELECT Valor FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
        Assert.Equal(87m, await Sql<decimal>("SELECT ValorPorMembro FROM eventos WHERE Id = @e", ("@e", evento.Id)));
        Assert.Equal(1, await Sql<int>("SELECT CAST(Status AS int) FROM Lancamentos WHERE Id = @l", ("@l", alvo)));
    }
}
