using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

/// <summary>
/// Issue #209 contra SQL Server real: redução e zeragem do valor com lançamento Pago geram devoluções, estorno avulso do participante,
/// remoção de quem pagou, exclusão bloqueada por valor retido, baixa com a trava do evento e o CHECK das devoluções.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class EventosDevolucoesSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private static DateOnly Ontem => TestHelpers.DataPagamentoPassada;

    private Task<List<Guid>> MembrosAsync(int quantidade) => EventosSqlSupport.MembrosAsync(Factory, quantidade);

    private Task<EventoDto> CriarAsync(object membros) => EventosSqlSupport.CriarAsync(Client, membros);

    private async Task<EventoDto> ObterAsync(Guid id) => (await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{id}", Json))!;

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<List<Lancamento>> LancamentosDe(Guid eventoId) =>
        Db(db => db.Lancamentos.AsNoTracking().Where(l => l.EventoId == eventoId).ToListAsync());

    private async Task PagarAsync(Guid lancamentoId, decimal? valorPago = null)
    {
        var resposta = valorPago is { } valor
            ? await TestHelpers.RegistrarPagamentoAsync(Client, lancamentoId, valor)
            : await TestHelpers.PagarAsync(Client, lancamentoId);
        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
    }

    private static Guid LancamentoDe(EventoDto dto, Guid membro) => dto.Lancamentos.Single(l => l.MembroId == membro).Id;

    private async Task<HttpResponseMessage> EditarAsync(EventoDto dto, object membros, decimal? transporte = null, decimal? alimentacao = null,
        decimal? seguro = null, string? motivo = null) =>
        await EventosTestKit.PutAsync(Client, dto.Id, EventosSqlSupport.Editar(dto, membros, transporte: transporte, alimentacao: alimentacao,
            seguro: seguro, motivo: motivo));

    private async Task<EventoDto> EditarOkAsync(EventoDto dto, object membros, decimal? transporte = null, decimal? alimentacao = null,
        decimal? seguro = null, string? motivo = null)
    {
        var resposta = await EditarAsync(dto, membros, transporte, alimentacao, seguro, motivo);
        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return await EventosTestKit.LerAsync(resposta);
    }

    private Task<HttpResponseMessage> EstornarAsync(Guid eventoId, Guid membroId, string versao, string motivo = "estorno do participante") =>
        Client.PostAsJsonAsync($"/api/Eventos/{eventoId}/Participantes/{membroId}/Estorno", new { motivo, versao });

    private Task<HttpResponseMessage> BaixarAsync(Guid devolucaoId, decimal valorPago, string? justificativa = null) =>
        Client.PostAsJsonAsync($"/api/Lancamentos/{devolucaoId}/Devolucao",
            new { dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago, justificativa });

    private async Task<SqlConnection> ConexaoAsync()
    {
        var conexao = new SqlConnection(Factory.ConnectionString);
        await conexao.OpenAsync();
        return conexao;
    }

    private async Task MoverParaOPassadoAsync(Guid eventoId)
    {
        var passado = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3).ToDateTime(TimeOnly.MinValue);
        await using var conexao = await ConexaoAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "UPDATE dbo.eventos SET DataEvento = @d WHERE Id = @id; UPDATE dbo.Lancamentos SET Vencimento = @d WHERE EventoId = @id AND LancamentoOrigemId IS NULL;";
        comando.Parameters.AddWithValue("@d", passado);
        comando.Parameters.AddWithValue("@id", eventoId);
        await comando.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Reducao_ComPago_GeraDevolucaoSoParaQuemPagouAcimaDoNovoValor_ENotifica()
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(m);
        await PagarAsync(LancamentoDe(dto, m[0]));
        await PagarAsync(LancamentoDe(dto, m[1]), 5m);
        dto = await ObterAsync(dto.Id);

        var atualizado = await EditarOkAsync(dto, m, transporte: 0m, motivo: "transporte patrocinado");

        Assert.Equal(10m, atualizado.ValorPorMembro);
        var devolucao = Assert.Single(atualizado.Devolucoes);
        Assert.Equal((m[0], LancamentoDe(dto, m[0]), 10m, StatusLancamento.Pendente, "transporte patrocinado"),
            (devolucao.MembroId, devolucao.LancamentoOrigemId, devolucao.Valor, devolucao.Status, devolucao.Motivo));

        var lancamentos = await LancamentosDe(dto.Id);
        var pago0 = lancamentos.Single(l => l.Id == LancamentoDe(dto, m[0]));
        var pago1 = lancamentos.Single(l => l.Id == LancamentoDe(dto, m[1]));
        var aberto = lancamentos.Single(l => l.Id == LancamentoDe(dto, m[2]));
        Assert.Equal((StatusLancamento.Pago, 20m, 20m), (pago0.Status, pago0.Valor, pago0.ValorPago!.Value));
        Assert.Equal((StatusLancamento.Pago, 20m, 5m), (pago1.Status, pago1.Valor, pago1.ValorPago!.Value));
        Assert.Equal((StatusLancamento.Pendente, 10m), (aberto.Status, aberto.Valor));

        var salva = lancamentos.Single(l => l.LancamentoOrigemId != null);
        Assert.Equal((TipoFluxoLancamento.Saida, m[0], DateOnly.FromDateTime(DateTime.UtcNow), true),
            (salva.TipoFluxo, salva.MembroId!.Value, salva.Vencimento, salva.Ativo));
        Assert.Equal($"Devolução - Evento - {dto.Titulo} - {dto.DataEvento:dd/MM/yyyy}", salva.Descricao);

        var notificacao = await Db(db => db.Notificacoes.AsNoTracking().SingleAsync(n => n.LancamentoId == salva.Id));
        Assert.Equal((m[0], TipoNotificacao.DevolucaoEvento, (Guid?)dto.Id), (notificacao.DestinatarioId, notificacao.Tipo, notificacao.EventoId));
        Assert.Equal($"Seu valor de R$ 10,00 referente ao evento {dto.Titulo} será devolvido.", notificacao.Mensagem);
    }

    [Fact]
    public async Task Zerar_ComPago_DevolveTudo_CancelaEmAberto_EMantemPagoEParticipantes()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);
        await PagarAsync(LancamentoDe(dto, m[0]));
        dto = await ObterAsync(dto.Id);

        var atualizado = await EditarOkAsync(dto, m, transporte: 0m, alimentacao: 0m, seguro: 0m, motivo: "evento passou a ser gratuito");

        Assert.Equal((0m, 20m), (atualizado.ValorPorMembro, Assert.Single(atualizado.Devolucoes).Valor));
        Assert.Equal(m.Order(), atualizado.Membros);
        Assert.Equal(LancamentoDe(dto, m[0]), Assert.Single(atualizado.Lancamentos).Id);
        var lancamentos = await LancamentosDe(dto.Id);
        Assert.False(lancamentos.Single(l => l.Id == LancamentoDe(dto, m[1])).Ativo);
        Assert.True(lancamentos.Single(l => l.Id == LancamentoDe(dto, m[0])).Ativo);
        var participantes = await Db(db => db.EventosMembros.AsNoTracking().Where(p => p.EventoId == dto.Id && p.Ativo).ToListAsync());
        Assert.Equal(LancamentoDe(dto, m[0]), participantes.Single(p => p.MembroId == m[0]).LancamentoId);
        Assert.Null(participantes.Single(p => p.MembroId == m[1]).LancamentoId);
    }

    [Fact]
    public async Task ReducoesSucessivas_NaoDevolvemDuasVezes_EEstornoDepoisNaoTemNadaADevolver()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await PagarAsync(dto.Lancamentos[0].Id);
        dto = await ObterAsync(dto.Id);

        dto = await EditarOkAsync(dto, m, transporte: 2m, motivo: "primeira redução");
        Assert.Equal(8m, Assert.Single(dto.Devolucoes).Valor);
        dto = await EditarOkAsync(dto, m, transporte: 0m, alimentacao: 0m, seguro: 0m, motivo: "zerado");

        Assert.Equal([8m, 12m], dto.Devolucoes.Select(d => d.Valor).Order());
        var estorno = await EstornarAsync(dto.Id, m, dto.Versao);
        Assert.Equal(HttpStatusCode.Conflict, estorno.StatusCode);
        Assert.Contains("Nada a devolver", await estorno.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reducao_ComPago_SemMotivo_Retorna400_SemAlterarNada()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await PagarAsync(dto.Lancamentos[0].Id);
        dto = await ObterAsync(dto.Id);

        var resposta = await EditarAsync(dto, m, transporte: 0m);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("devoluções", await resposta.Content.ReadAsStringAsync());
        Assert.Equal((dto.Versao, 20m), ((await ObterAsync(dto.Id)).Versao, (await ObterAsync(dto.Id)).ValorPorMembro));
        Assert.Single(await LancamentosDe(dto.Id));
    }

    [Fact]
    public async Task Aumento_ComPago_Retorna409()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await PagarAsync(dto.Lancamentos[0].Id);
        dto = await ObterAsync(dto.Id);

        var resposta = await EditarAsync(dto, m, transporte: 15m, motivo: "aumento");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Contains("só pode diminuir", await resposta.Content.ReadAsStringAsync());
        Assert.Equal(dto.Versao, (await ObterAsync(dto.Id)).Versao);
    }

    [Fact]
    public async Task Reducao_ComPagoHistoricoSemDados_Retorna409_PedindoORegistroDoPagamento()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await using (var conexao = await ConexaoAsync())
        await using (var comando = conexao.CreateCommand())
        {
            comando.CommandText = "UPDATE dbo.Lancamentos SET Status = 1 WHERE Id = @id";
            comando.Parameters.AddWithValue("@id", dto.Lancamentos[0].Id);
            await comando.ExecuteNonQueryAsync();
        }

        dto = await ObterAsync(dto.Id);
        var resposta = await EditarAsync(dto, m, transporte: 0m, motivo: "redução");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Contains("/Pagamento", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Estorno_DevolveOValorRetido_UmaVezSo_EValidaParticipanteEVersao()
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(new[] { m[0], m[1] });
        await PagarAsync(LancamentoDe(dto, m[0]), 18m);
        dto = await ObterAsync(dto.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await EstornarAsync(dto.Id, m[2], dto.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EstornarAsync(dto.Id, m[1], dto.Versao)).StatusCode);

        var resposta = await EstornarAsync(dto.Id, m[0], dto.Versao, "desistiu do passeio");

        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var atualizado = await EventosTestKit.LerAsync(resposta);
        var devolucao = Assert.Single(atualizado.Devolucoes);
        Assert.Equal((m[0], 18m, "desistiu do passeio"), (devolucao.MembroId, devolucao.Valor, devolucao.Motivo));
        Assert.Equal(new[] { m[0], m[1] }.Order(), atualizado.Membros);
        Assert.NotEqual(dto.Versao, atualizado.Versao);
        Assert.Equal(HttpStatusCode.Conflict, (await EstornarAsync(dto.Id, m[0], dto.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EstornarAsync(dto.Id, m[0], atualizado.Versao)).StatusCode);
        Assert.Equal(1, await Db(db => db.Notificacoes.CountAsync(n => n.Tipo == TipoNotificacao.DevolucaoEvento && n.EventoId == dto.Id)));
    }

    [Fact]
    public async Task RemoverQuemPagou_AntesDaData_SoDepoisDoEstornoConcluido()
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(m);
        await PagarAsync(LancamentoDe(dto, m[0]));
        dto = await ObterAsync(dto.Id);

        // Quem não pagou sai normalmente, mesmo com outro participante pago.
        dto = await EditarOkAsync(dto, new[] { m[0], m[1] }, motivo: "saiu");
        Assert.False((await LancamentosDe(dto.Id)).Single(l => l.MembroId == m[2]).Ativo);

        var bloqueada = await EditarAsync(dto, new[] { m[1] }, motivo: "remover pagante");
        Assert.Equal(HttpStatusCode.Conflict, bloqueada.StatusCode);
        Assert.Contains("Estorno", await bloqueada.Content.ReadAsStringAsync());

        var estorno = await EventosTestKit.LerAsync(await EstornarAsync(dto.Id, m[0], dto.Versao));
        Assert.Equal(HttpStatusCode.Conflict, (await EditarAsync(estorno, new[] { m[1] }, motivo: "remover pagante")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await BaixarAsync(estorno.Devolucoes[0].Id, 20m)).StatusCode);
        var atual = await ObterAsync(dto.Id);
        var removido = await EditarOkAsync(atual, new[] { m[1] }, motivo: "remover pagante");

        Assert.Equal([m[1]], removido.Membros);
        var pago = (await LancamentosDe(dto.Id)).Single(l => l.MembroId == m[0] && l.LancamentoOrigemId == null);
        Assert.Equal((true, StatusLancamento.Pago), (pago.Ativo, pago.Status));
    }

    [Fact]
    public async Task RemoverQuemPagou_NoDiaDoEventoOuDepois_SaiSemDevolucao()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);
        await PagarAsync(LancamentoDe(dto, m[0]));
        await MoverParaOPassadoAsync(dto.Id);
        dto = await ObterAsync(dto.Id);

        var removido = await EditarOkAsync(dto, new[] { m[1] }, motivo: "não compareceu");

        Assert.Equal([m[1]], removido.Membros);
        Assert.Empty(removido.Devolucoes);
        var pago = (await LancamentosDe(dto.Id)).Single(l => l.Id == LancamentoDe(dto, m[0]));
        Assert.Equal((true, StatusLancamento.Pago), (pago.Ativo, pago.Status));
    }

    [Fact]
    public async Task Excluir_ComValorRetidoOuDevolucaoPendente_Retorna409_EDepoisDaBaixaExclui_PreservandoOCaixa()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m);
        await PagarAsync(LancamentoDe(dto, m[0]));
        dto = await ObterAsync(dto.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.DeleteAsync(Client, dto.Id, "excluir", dto.Versao)).StatusCode);

        dto = await EditarOkAsync(dto, m, transporte: 0m, alimentacao: 0m, seguro: 0m, motivo: "cancelado");
        var pendente = await EventosTestKit.DeleteAsync(Client, dto.Id, "excluir", dto.Versao);
        Assert.Equal(HttpStatusCode.Conflict, pendente.StatusCode);
        Assert.Contains("valor retido", await pendente.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await BaixarAsync(dto.Devolucoes[0].Id, 19m, "tarifa do Pix")).StatusCode);
        dto = await ObterAsync(dto.Id);
        var excluido = await EventosTestKit.DeleteAsync(Client, dto.Id, "excluir", dto.Versao);

        Assert.Equal(HttpStatusCode.NoContent, excluido.StatusCode);
        var lancamentos = await LancamentosDe(dto.Id);
        Assert.All(lancamentos.Where(l => l.Status == StatusLancamento.Pago), l => Assert.True(l.Ativo));
        Assert.Equal(2, lancamentos.Count(l => l.Ativo));
        Assert.All(lancamentos.Where(l => l.Status != StatusLancamento.Pago), l => Assert.False(l.Ativo));
    }

    [Fact]
    public async Task Baixa_ComTravaDoEvento_AlteraAVersao_EExigeJustificativaQuandoOValorDifere()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        await PagarAsync(dto.Lancamentos[0].Id);
        dto = await ObterAsync(dto.Id);
        dto = await EditarOkAsync(dto, m, transporte: 0m, motivo: "redução");
        var devolucao = dto.Devolucoes[0];

        Assert.Equal(HttpStatusCode.BadRequest, (await BaixarAsync(devolucao.Id, 9m)).StatusCode);
        var resposta = await BaixarAsync(devolucao.Id, 9m, "devolvido em espécie");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var depois = await ObterAsync(dto.Id);
        Assert.NotEqual(dto.Versao, depois.Versao);
        Assert.Equal((StatusLancamento.Pago, (decimal?)9m, "devolvido em espécie"),
            (depois.Devolucoes[0].Status, depois.Devolucoes[0].ValorDevolvido, depois.Devolucoes[0].Justificativa));
    }

    [Fact]
    public async Task Participante_VeAsPropriasDevolucoesEmMeEventos()
    {
        var (participante, participanteId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DS");
        var dto = await CriarAsync(participanteId);
        await PagarAsync(dto.Lancamentos[0].Id);
        dto = await ObterAsync(dto.Id);
        dto = await EditarOkAsync(dto, participanteId, transporte: 0m, motivo: "redução");
        Assert.Equal(HttpStatusCode.OK, (await BaixarAsync(dto.Devolucoes[0].Id, 10m)).StatusCode);

        var meu = (await participante.GetFromJsonAsync<EventoParticipanteDto>($"/api/Me/Eventos/{dto.Id}", Json))!;

        var devolucao = Assert.Single(meu.MinhasDevolucoes);
        Assert.Equal((10m, StatusLancamento.Pago, (decimal?)10m, (DateOnly?)Ontem), (devolucao.Valor, devolucao.Status, devolucao.ValorDevolvido, devolucao.DataDevolucao));
        Assert.Equal(StatusLancamento.Pago, meu.MinhaCobranca!.Status);
    }

    [Fact]
    public async Task CheckDeDevolucao_RecusaDevolucaoQueNaoEhSaidaDeMembroDeEvento()
    {
        var m = (await MembrosAsync(1))[0];
        var dto = await CriarAsync(m);
        var origem = dto.Lancamentos[0].Id;

        await using var conexao = await ConexaoAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = """
            INSERT INTO dbo.Lancamentos (Id, MembroId, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, Ativo, EventoId, LancamentoOrigemId, DevolucaoMotivo)
            VALUES (NEWID(), @membro, 0, 5, '2026-11-17', 0, 0, SYSUTCDATETIME(), 1, @evento, @origem, 'entrada não pode');
            """;
        comando.Parameters.AddWithValue("@membro", m);
        comando.Parameters.AddWithValue("@evento", dto.Id);
        comando.Parameters.AddWithValue("@origem", origem);

        var erro = await Assert.ThrowsAsync<SqlException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal(547, erro.Number);
        Assert.Contains("CK_Lancamentos_Devolucao", erro.Message);
    }
}
