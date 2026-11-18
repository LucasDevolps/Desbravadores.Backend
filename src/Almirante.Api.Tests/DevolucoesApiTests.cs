using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Almirante.Api.Tests;

/// <summary>
/// Issue #209: baixa e correção da devolução de evento em /api/Lancamentos/{id}/Devolucao, bloqueios dos outros endpoints de lançamento
/// sobre devoluções e validação do estorno de participante. Componente em processo (EF InMemory): o caminho com trava do evento e as
/// regras de redução/estorno estão em <see cref="EventosDevolucoesSqlServerTests"/>.
/// </summary>
public sealed class DevolucoesApiTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DateOnly Ontem => TestHelpers.DataPagamentoPassada;

    private sealed record Cenario(HttpClient Client, Guid TesoureiroId, Lancamento Pago, Lancamento Devolucao);

    /// <summary>Cobrança de evento paga (R$ 20,00) com uma devolução pendente de <paramref name="esperado"/>.</summary>
    private async Task<Cenario> SemearAsync(decimal esperado = 15m)
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, $"Devolução {Guid.NewGuid():N}", "DS");
        var (client, tesoureiroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var eventoId = Guid.NewGuid();
        var pago = new Lancamento
        {
            Id = Guid.NewGuid(), MembroId = membro.Id, Categoria = CategoriaLancamento.Evento, TipoFluxo = TipoFluxoLancamento.Entrada,
            Descricao = "Evento - Passeio - 01/12/2026", Valor = 20m, Vencimento = new DateOnly(2026, 12, 1), Status = StatusLancamento.Pago,
            DataPagamento = Ontem, ValorPago = 20m, PagamentoRegistradoPorUsuarioId = tesoureiroId, PagamentoRegistradoEmUtc = DateTime.UtcNow,
            EventoId = eventoId,
        };
        var devolucao = Services.DevolucaoRegras.Nova(pago, esperado, "Passeio", new DateOnly(2026, 12, 1), Ontem, "valor reduzido", DateTime.UtcNow);
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            db.Lancamentos.AddRange(pago, devolucao);
            return await db.SaveChangesAsync();
        });
        return new Cenario(client, tesoureiroId, pago, devolucao);
    }

    private Task<Lancamento> LerAsync(Guid id) =>
        TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id));

    private static Task<HttpResponseMessage> BaixarAsync(HttpClient client, Guid id, decimal valorPago, string? justificativa = null,
        DateOnly? data = null, bool correcao = false)
    {
        var corpo = new { dataPagamento = (data ?? Ontem).ToString("yyyy-MM-dd"), valorPago, justificativa };
        return correcao
            ? client.PutAsJsonAsync($"/api/Lancamentos/{id}/Devolucao", corpo)
            : client.PostAsJsonAsync($"/api/Lancamentos/{id}/Devolucao", corpo);
    }

    [Fact]
    public async Task Baixa_ValorIgualAoEsperado_SemJustificativa_MarcaPagaComResponsavel()
    {
        var c = await SemearAsync(15m);

        var resposta = await BaixarAsync(c.Client, c.Devolucao.Id, 15m);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var dto = (await resposta.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        Assert.Equal((StatusLancamento.Pago, 15m, Ontem, (string?)null, c.TesoureiroId),
            (dto.Status, dto.ValorPago!.Value, dto.DataPagamento!.Value, dto.DevolucaoJustificativa, dto.PagamentoRegistradoPorUsuarioId!.Value));
        Assert.Equal((c.Pago.Id, "valor reduzido", TipoFluxoLancamento.Saida), (dto.LancamentoOrigemId!.Value, dto.DevolucaoMotivo, dto.TipoFluxo));
        var salvo = await LerAsync(c.Devolucao.Id);
        Assert.Equal((StatusLancamento.Pago, 15m, c.TesoureiroId), (salvo.Status, salvo.ValorPago!.Value, salvo.AtualizadoPorUsuarioId!.Value));
        Assert.Equal((StatusLancamento.Pago, 20m), ((await LerAsync(c.Pago.Id)).Status, (await LerAsync(c.Pago.Id)).ValorPago!.Value));
    }

    [Fact]
    public async Task Baixa_ValorIgualComJustificativa_GuardaAJustificativaTrimada()
    {
        var c = await SemearAsync(15m);

        var resposta = await BaixarAsync(c.Client, c.Devolucao.Id, 15m, "  devolvido por Pix  ");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("devolvido por Pix", (await LerAsync(c.Devolucao.Id)).DevolucaoJustificativa);
    }

    [Fact]
    public async Task Baixa_ValorMenorSemJustificativa_Retorna400_SemAlterarADevolucao()
    {
        var c = await SemearAsync(15m);

        var resposta = await BaixarAsync(c.Client, c.Devolucao.Id, 14m);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("justificativa", await resposta.Content.ReadAsStringAsync());
        var salvo = await LerAsync(c.Devolucao.Id);
        Assert.Equal((StatusLancamento.Pendente, (decimal?)null), (salvo.Status, salvo.ValorPago));
    }

    [Fact]
    public async Task Baixa_ValorMenorComJustificativa_GuardaValorRealEJustificativa()
    {
        var c = await SemearAsync(15m);

        var resposta = await BaixarAsync(c.Client, c.Devolucao.Id, 12.5m, "abc");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var salvo = await LerAsync(c.Devolucao.Id);
        Assert.Equal((StatusLancamento.Pago, 15m, 12.5m, "abc"), (salvo.Status, salvo.Valor, salvo.ValorPago!.Value, salvo.DevolucaoJustificativa));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("ab")]
    [InlineData("   ")]
    [InlineData("😀😀😀")]
    public async Task Baixa_JustificativaInvalida_Retorna400MesmoComValorIgual(string justificativa)
    {
        var c = await SemearAsync(15m);

        var resposta = await BaixarAsync(c.Client, c.Devolucao.Id, 15m, justificativa);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("justificativa", await resposta.Content.ReadAsStringAsync());
        Assert.Null((await LerAsync(c.Devolucao.Id)).ValorPago);
    }

    [Fact]
    public async Task Baixa_JustificativaAcimaDe255Caracteres_Retorna400()
    {
        var c = await SemearAsync(15m);

        var resposta = await BaixarAsync(c.Client, c.Devolucao.Id, 10m, new string('a', 256));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("255", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Baixa_ValorAcimaDoEsperado_Retorna400MesmoComJustificativa()
    {
        var c = await SemearAsync(15m);

        var resposta = await BaixarAsync(c.Client, c.Devolucao.Id, 15.01m, "devolvi a mais");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("valorPago", await resposta.Content.ReadAsStringAsync());
        Assert.Null((await LerAsync(c.Devolucao.Id)).ValorPago);
    }

    public static TheoryData<string> CorposInvalidos => new()
    {
        """{"valorPago": 15}""",
        """{"dataPagamento": "2026-10-01"}""",
        """{"dataPagamento": "2026-10-01", "valorPago": 0}""",
        """{"dataPagamento": "2026-10-01", "valorPago": 10.001}""",
        """{"dataPagamento": "2026-10-01", "valorPago": 99999999999999999.99}""",
        """{"dataPagamento": "2999-01-01", "valorPago": 15}""",
    };

    [Theory]
    [MemberData(nameof(CorposInvalidos))]
    public async Task Baixa_DadosInvalidosOuAusentes_Retorna400(string corpo)
    {
        var c = await SemearAsync(15m);

        using var conteudo = new StringContent(corpo, System.Text.Encoding.UTF8, "application/json");
        var resposta = await c.Client.PostAsync($"/api/Lancamentos/{c.Devolucao.Id}/Devolucao", conteudo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Null((await LerAsync(c.Devolucao.Id)).ValorPago);
    }

    [Fact]
    public async Task Baixa_Duplicada_Retorna409_ECorrecaoSemBaixa_Retorna409()
    {
        var c = await SemearAsync(15m);

        var semBaixa = await BaixarAsync(c.Client, c.Devolucao.Id, 15m, correcao: true);
        Assert.Equal(HttpStatusCode.Conflict, semBaixa.StatusCode);
        Assert.Contains("POST /api/Lancamentos/{id}/Devolucao", await semBaixa.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await BaixarAsync(c.Client, c.Devolucao.Id, 15m)).StatusCode);
        var duplicada = await BaixarAsync(c.Client, c.Devolucao.Id, 10m, "outra vez");
        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);
        Assert.Contains("PUT /api/Lancamentos/{id}/Devolucao", await duplicada.Content.ReadAsStringAsync());
        Assert.Equal(15m, (await LerAsync(c.Devolucao.Id)).ValorPago);
    }

    [Fact]
    public async Task Correcao_TrocaValorDataEJustificativa_EApagaAJustificativaQuandoVoltaAoEsperado()
    {
        var c = await SemearAsync(15m);
        Assert.Equal(HttpStatusCode.OK, (await BaixarAsync(c.Client, c.Devolucao.Id, 15m)).StatusCode);

        var reduzida = await BaixarAsync(c.Client, c.Devolucao.Id, 13m, "tarifa bancária", Ontem.AddDays(-2), correcao: true);
        Assert.Equal(HttpStatusCode.OK, reduzida.StatusCode);
        var salvo = await LerAsync(c.Devolucao.Id);
        Assert.Equal((13m, Ontem.AddDays(-2), "tarifa bancária"), (salvo.ValorPago!.Value, salvo.DataPagamento!.Value, salvo.DevolucaoJustificativa));

        Assert.Equal(HttpStatusCode.BadRequest, (await BaixarAsync(c.Client, c.Devolucao.Id, 12m, correcao: true)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await BaixarAsync(c.Client, c.Devolucao.Id, 15m, correcao: true)).StatusCode);
        salvo = await LerAsync(c.Devolucao.Id);
        Assert.Equal((15m, (string?)null), (salvo.ValorPago!.Value, salvo.DevolucaoJustificativa));
    }

    [Fact]
    public async Task Baixa_EmLancamentoQueNaoEhDevolucao_Retorna409()
    {
        var c = await SemearAsync();

        var resposta = await BaixarAsync(c.Client, c.Pago.Id, 20m);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Contains("/Pagamento", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Baixa_LancamentoInexistente_Retorna404()
    {
        var c = await SemearAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(c.Client, Guid.NewGuid(), 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(c.Client, Guid.NewGuid(), 10m, correcao: true)).StatusCode);
    }

    [Fact]
    public async Task Baixa_SemPermissaoDePagamento_Retorna403()
    {
        var c = await SemearAsync();
        var (membro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");

        Assert.Equal(HttpStatusCode.Forbidden, (await BaixarAsync(membro, c.Devolucao.Id, 15m)).StatusCode);
        Assert.Null((await LerAsync(c.Devolucao.Id)).ValorPago);
    }

    [Fact]
    public async Task OutrosEndpointsDeLancamento_RecusamDevolucao_Com409()
    {
        var c = await SemearAsync();

        var put = await c.Client.PutAsJsonAsync($"/api/Lancamentos/{c.Devolucao.Id}", new { descricao = "outra" });
        var pagamento = await TestHelpers.RegistrarPagamentoAsync(c.Client, c.Devolucao.Id, 15m);
        var correcaoPagamento = await c.Client.PutAsJsonAsync($"/api/Lancamentos/{c.Devolucao.Id}/Pagamento",
            new { dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago = 15m });

        foreach (var resposta in new[] { put, pagamento, correcaoPagamento })
        {
            Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
            Assert.Contains("/Devolucao", await resposta.Content.ReadAsStringAsync());
        }

        var salvo = await LerAsync(c.Devolucao.Id);
        Assert.Equal((StatusLancamento.Pendente, (decimal?)null), (salvo.Status, salvo.ValorPago));
        Assert.Equal("Devolução - Evento - Passeio - 01/12/2026", salvo.Descricao);
    }

    [Fact]
    public async Task CorrecaoDoValorPago_DeCobrancaComDevolucao_Retorna409_MasADataAindaMuda()
    {
        var c = await SemearAsync();

        var valor = await c.Client.PutAsJsonAsync($"/api/Lancamentos/{c.Pago.Id}/Pagamento",
            new { dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago = 18m });
        Assert.Equal(HttpStatusCode.Conflict, valor.StatusCode);
        Assert.Contains("devolução", await valor.Content.ReadAsStringAsync());
        Assert.Equal(20m, (await LerAsync(c.Pago.Id)).ValorPago);

        var data = await c.Client.PutAsJsonAsync($"/api/Lancamentos/{c.Pago.Id}/Pagamento",
            new { dataPagamento = Ontem.AddDays(-3).ToString("yyyy-MM-dd"), valorPago = 20m });
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        Assert.Equal(Ontem.AddDays(-3), (await LerAsync(c.Pago.Id)).DataPagamento);
    }

    [Fact]
    public async Task Consulta_ExpoeOrigemEMotivoDaDevolucao()
    {
        var c = await SemearAsync(15m);

        var dto = (await c.Client.GetFromJsonAsync<LancamentoDto>($"/api/Lancamentos/{c.Devolucao.Id}", Json))!;

        Assert.Equal((c.Pago.Id, "valor reduzido", (string?)null, TipoFluxoLancamento.Saida, 15m),
            (dto.LancamentoOrigemId!.Value, dto.DevolucaoMotivo, dto.DevolucaoJustificativa, dto.TipoFluxo, dto.ValorRestante!.Value));
        var cobranca = (await c.Client.GetFromJsonAsync<LancamentoDto>($"/api/Lancamentos/{c.Pago.Id}", Json))!;
        Assert.Equal(((Guid?)null, (string?)null), (cobranca.LancamentoOrigemId, cobranca.DevolucaoMotivo));
    }

    public static TheoryData<string> EstornosInvalidos => new()
    {
        """{"versao": "AAAAAAAAB9E="}""",
        """{"motivo": "   ", "versao": "AAAAAAAAB9E="}""",
        """{"motivo": "estorno"}""",
        """{"motivo": "estorno", "versao": "invalida"}""",
        """{"motivo": "😀", "versao": "AAAAAAAAB9E="}""",
    };

    [Theory]
    [MemberData(nameof(EstornosInvalidos))]
    public async Task Estorno_CorpoInvalido_Retorna400(string corpo)
    {
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");

        using var conteudo = new StringContent(corpo, System.Text.Encoding.UTF8, "application/json");
        var resposta = await client.PostAsync($"/api/Eventos/{Guid.NewGuid()}/Participantes/{Guid.NewGuid()}/Estorno", conteudo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Fact]
    public async Task Estorno_MotivoAcimaDe255_Retorna400()
    {
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");

        var resposta = await client.PostAsJsonAsync($"/api/Eventos/{Guid.NewGuid()}/Participantes/{Guid.NewGuid()}/Estorno",
            new { motivo = new string('m', 256), versao = "AAAAAAAAB9E=" });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("255", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public void TextosDeNotificacao_ResolvidosPelaInjecaoDaApi_VemDoResxEmbutido()
    {
        using var escopo = factory.Services.CreateScope();
        var textos = escopo.ServiceProvider.GetRequiredService<IStringLocalizer<NotificacoesTextos>>();

        var mensagem = textos[NotificacoesTextos.DevolucaoEventoMensagem, "R$ 5,00", "Reunião"];

        Assert.False(mensagem.ResourceNotFound);
        Assert.Equal("Seu valor de R$ 5,00 referente ao evento Reunião será devolvido.", mensagem.Value);
    }
}
