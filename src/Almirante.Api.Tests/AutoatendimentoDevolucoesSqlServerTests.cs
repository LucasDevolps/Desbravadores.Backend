using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class AutoatendimentoDevolucoesSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Admin => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private sealed record Cenario(HttpClient Ana, Guid AnaId, HttpClient Bruno, Guid BrunoId, EventoDto Evento, EventoDto SemDevolucao);

    private static async Task Esperar(HttpStatusCode esperado, HttpResponseMessage resposta) =>
        Assert.True(resposta.StatusCode == esperado, $"{(int)resposta.StatusCode}: {await resposta.Content.ReadAsStringAsync()}");

    private async Task<EventoDto> ObterAsync(Guid id) => (await Admin.GetFromJsonAsync<EventoDto>($"/api/Eventos/{id}", Json))!;

    private async Task<EventoDto> ReduzirAsync(EventoDto dto, List<Guid> membros, decimal? transporte = null, decimal? alimentacao = null)
    {
        var resposta = await EventosTestKit.PutAsync(Admin, dto.Id,
            EventosSqlSupport.Editar(dto, membros, transporte: transporte, alimentacao: alimentacao, motivo: "patrocínio do passeio"));
        await Esperar(HttpStatusCode.OK, resposta);
        return await EventosTestKit.LerAsync(resposta);
    }

    private async Task<Cenario> MontarAsync()
    {
        var (ana, anaId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DS");
        var (bruno, brunoId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DS");
        var membros = new List<Guid> { anaId, brunoId };
        var evento = await EventosSqlSupport.CriarAsync(Admin, membros);
        foreach (var cobranca in evento.Lancamentos)
        {
            await Esperar(HttpStatusCode.OK, await TestHelpers.PagarAsync(Admin, cobranca.Id));
        }

        evento = await ReduzirAsync(await ObterAsync(evento.Id), membros, transporte: 0m);
        evento = await ReduzirAsync(evento, membros, alimentacao: 0m);
        var primeiraDaAna = evento.Devolucoes.Single(d => d.MembroId == anaId && d.Valor == 10m);
        await Esperar(HttpStatusCode.OK, await Admin.PostAsJsonAsync($"/api/Lancamentos/{primeiraDaAna.Id}/Devolucao",
            new { dataPagamento = TestHelpers.DataPagamentoPassada.ToString("yyyy-MM-dd"), valorPago = 7m, justificativa = "devolvido em espécie" }));

        var semDevolucao = await EventosSqlSupport.CriarAsync(Admin, anaId, local: "Sede do clube");
        return new Cenario(ana, anaId, bruno, brunoId, await ObterAsync(evento.Id), semDevolucao);
    }

    [Fact]
    public async Task MultiplasDevolucoes_ParcialEPendente_AparecemSemDuplicarENaoVazamParaOutroMembro()
    {
        var c = await MontarAsync();

        var meu = (await c.Ana.GetFromJsonAsync<EventoParticipanteDto>($"/api/Me/Eventos/{c.Evento.Id}", Json))!;

        Assert.Equal((c.Evento.Titulo, c.Evento.Descricao), (meu.Titulo, meu.Descricao));
        Assert.Equal(2, meu.MinhasDevolucoes.Count);
        var parcial = meu.MinhasDevolucoes.Single(d => d.Valor == 10m);
        var pendente = meu.MinhasDevolucoes.Single(d => d.Valor == 8m);
        Assert.Equal((StatusLancamento.Pago, (decimal?)7m, (DateOnly?)TestHelpers.DataPagamentoPassada, "devolvido em espécie"),
            (parcial.Status, parcial.ValorDevolvido, parcial.DataDevolucao, parcial.Justificativa));
        Assert.Equal((StatusLancamento.Pendente, (decimal?)null, (DateOnly?)null, (string?)null),
            (pendente.Status, pendente.ValorDevolvido, pendente.DataDevolucao, pendente.Justificativa));
        Assert.Equal((20m, StatusLancamento.Pago), (meu.MinhaCobranca!.Valor, meu.MinhaCobranca.Status));

        var doBruno = (await c.Bruno.GetFromJsonAsync<EventoParticipanteDto>($"/api/Me/Eventos/{c.Evento.Id}", Json))!;
        Assert.Equal([8m, 10m], doBruno.MinhasDevolucoes.Select(d => d.Valor).Order());
        Assert.All(doBruno.MinhasDevolucoes, d => Assert.Equal((StatusLancamento.Pendente, (decimal?)null), (d.Status, d.ValorDevolvido)));
    }

    [Fact]
    public async Task ListaEDetalhe_DevolvemOMesmoConteudo_EEventoSemDevolucaoTemListaVazia()
    {
        var c = await MontarAsync();

        var lista = (await c.Ana.GetFromJsonAsync<JsonNode>("/api/Me/Eventos", Json))!["items"]!.AsArray();
        var detalhe = (await c.Ana.GetFromJsonAsync<JsonNode>($"/api/Me/Eventos/{c.Evento.Id}", Json))!;

        var daLista = lista.Single(e => (Guid)e!["id"]! == c.Evento.Id)!;
        Assert.True(JsonNode.DeepEquals(daLista, detalhe), $"{daLista.ToJsonString()}\n{detalhe.ToJsonString()}");
        var semDevolucao = lista.Single(e => (Guid)e!["id"]! == c.SemDevolucao.Id)!;
        Assert.Empty(semDevolucao["minhasDevolucoes"]!.AsArray());
        Assert.Equal("Pendente", (string)semDevolucao["minhaCobranca"]!["status"]!);
    }

    [Fact]
    public async Task ContratoJson_DasDevolucoes_TemCamposStatusEmTextoEDatasCivis()
    {
        var c = await MontarAsync();

        var detalhe = (await c.Ana.GetFromJsonAsync<JsonNode>($"/api/Me/Eventos/{c.Evento.Id}", Json))!;

        foreach (var devolucao in detalhe["minhasDevolucoes"]!.AsArray())
        {
            Assert.Equal(["valor", "vencimento", "status", "valorDevolvido", "dataDevolucao", "justificativa"],
                devolucao!.AsObject().Select(p => p.Key));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", (string)devolucao["vencimento"]!);
            Assert.Contains((string)devolucao["status"]!, new[] { "Pendente", "Pago", "Atrasado" });
        }

        var parcial = detalhe["minhasDevolucoes"]!.AsArray().Single(d => (decimal)d!["valor"]! == 10m)!;
        Assert.Equal(TestHelpers.DataPagamentoPassada.ToString("yyyy-MM-dd"), (string)parcial["dataDevolucao"]!);
    }

    [Fact]
    public async Task NaoParticipante_Recebe404ENaoVeOEventoNaLista()
    {
        var c = await MontarAsync();
        var (outro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DS");

        await Esperar(HttpStatusCode.NotFound, await outro.GetAsync($"/api/Me/Eventos/{c.Evento.Id}"));
        var lista = (await outro.GetFromJsonAsync<JsonNode>("/api/Me/Eventos", Json))!["items"]!.AsArray();
        Assert.DoesNotContain(lista, e => (Guid)e!["id"]! == c.Evento.Id);
    }

    [Fact]
    public async Task NotificacoesDeDevolucao_ChegamPorRestEmPtBrComEventoId()
    {
        var c = await MontarAsync();

        var historico = (await c.Ana.GetFromJsonAsync<JsonNode>("/api/Notificacoes?filtro=Todas&pageSize=50", Json))!;
        var devolucoes = historico["items"]!.AsArray().Where(n => (string)n!["tipo"]! == nameof(TipoNotificacao.DevolucaoEvento)).ToList();

        Assert.Equal(2, devolucoes.Count);
        Assert.All(devolucoes, n => Assert.Equal(c.Evento.Id, (Guid)n!["eventoId"]!));
        Assert.Equal(
            [$"Seu valor de R$ 10,00 referente ao evento {c.Evento.Titulo} será devolvido.", $"Seu valor de R$ 8,00 referente ao evento {c.Evento.Titulo} será devolvido."],
            devolucoes.Select(n => (string)n!["mensagem"]!).OrderByDescending(m => m.Contains("10,00")));
        Assert.True((int)historico["naoLidas"]! >= 2);

        var pendentes = (await c.Ana.GetFromJsonAsync<JsonNode>("/api/Notificacoes/Pendentes?pageSize=50", Json))!;
        Assert.Equal(2, pendentes["items"]!.AsArray().Count(n => (string)n!["tipo"]! == nameof(TipoNotificacao.DevolucaoEvento)));
        var doBruno = (await c.Bruno.GetFromJsonAsync<JsonNode>("/api/Notificacoes?filtro=Todas&pageSize=50", Json))!;
        Assert.DoesNotContain(doBruno["items"]!.AsArray(), n => devolucoes.Any(d => (Guid)d!["id"]! == (Guid)n!["id"]!));
    }
}
