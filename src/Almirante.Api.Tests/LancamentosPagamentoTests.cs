using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

public sealed class LancamentosPagamentoTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DateOnly Ontem => TestHelpers.DataPagamentoPassada;

    private async Task<(HttpClient Client, Guid UsuarioId, LancamentoDto Lancamento)> CriarAsync(decimal valor = 50m, string tipoFluxo = "Entrada")
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, $"Pagamento {Guid.NewGuid():N}", "DS");
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var criado = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", new
        {
            membroId = membro.Id, finalidade = "Mensalidade", descricao = "Outubro", categoria = "Clube", tipoFluxo, valor,
            vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = false,
        });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        return (client, usuarioId, (await criado.Content.ReadFromJsonAsync<LancamentoDto>(Json))!);
    }

    private Task<Lancamento> LerAsync(Guid id) =>
        TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id));

    private static Task<HttpResponseMessage> AtualizarPagamentoAsync(HttpClient client, Guid id, object corpo) =>
        client.PutAsJsonAsync($"/api/Lancamentos/{id}/Pagamento", corpo);

    [Fact]
    public async Task Registrar_PagamentoCompleto_MarcaPago_GuardaDataValorEResponsavel()
    {
        var (client, usuarioId, lancamento) = await CriarAsync(50m);

        var resposta = await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var dto = (await resposta.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        Assert.Equal((StatusLancamento.Pago, Ontem, 50m, 0m, usuarioId), (dto.Status, dto.DataPagamento!.Value, dto.ValorPago!.Value, dto.ValorRestante!.Value, dto.PagamentoRegistradoPorUsuarioId!.Value));
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((StatusLancamento.Pago, Ontem, 50m, 50m, usuarioId, usuarioId), (salvo.Status, salvo.DataPagamento!.Value, salvo.ValorPago!.Value, salvo.Valor, salvo.PagamentoRegistradoPorUsuarioId!.Value, salvo.AtualizadoPorUsuarioId!.Value));
        Assert.NotNull(salvo.PagamentoRegistradoEmUtc);
        Assert.Equal(salvo.PagamentoRegistradoEmUtc, salvo.DataAtualizacao);
    }

    [Fact]
    public async Task Registrar_ValorPagoMenorQueOPrevisto_GuardaOValorReal_SemAlterarOValorOriginal()
    {
        var (client, _, lancamento) = await CriarAsync(80m);

        var resposta = await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 72.35m, Ontem);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var dto = (await resposta.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        Assert.Equal((80m, 72.35m, 7.65m), (dto.Valor, dto.ValorPago!.Value, dto.ValorRestante!.Value));
    }

    [Fact]
    public async Task Registrar_DataDePagamentoDiferenteDoVencimento_GuardaADataEfetiva()
    {
        var (client, _, lancamento) = await CriarAsync();
        var dataEfetiva = Ontem.AddDays(-20);

        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, dataEfetiva)).StatusCode);

        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal(dataEfetiva, salvo.DataPagamento);
        Assert.Equal(lancamento.Vencimento, salvo.Vencimento);
        Assert.NotEqual(salvo.Vencimento, salvo.DataPagamento);
    }

    public static TheoryData<string> CorposInvalidos => new()
    {
        """{"valorPago": 50}""",
        """{"dataPagamento": null, "valorPago": 50}""",
        """{"dataPagamento": "2026-10-01"}""",
        """{"dataPagamento": "2026-10-01", "valorPago": 0}""",
        """{"dataPagamento": "2026-10-01", "valorPago": -10}""",
        """{"dataPagamento": "2026-10-01", "valorPago": 10.001}""",
        """{"dataPagamento": "2026-10-01", "valorPago": 99999999999999999.99}""",
        """{"dataPagamento": "2999-01-01", "valorPago": 10}""",
        """{"dataPagamento": "01/10/2026", "valorPago": 10}""",
        """{"dataPagamento": "2026-02-30", "valorPago": 10}""",
        """{"dataPagamento": "2026-10-01", "valorPago": "abc"}""",
    };

    [Theory]
    [MemberData(nameof(CorposInvalidos))]
    public async Task Registrar_DadosInvalidosOuAusentes_Retorna400_SemAlterarOLancamento(string corpo)
    {
        var (client, _, lancamento) = await CriarAsync();

        using var conteudo = new StringContent(corpo, System.Text.Encoding.UTF8, "application/json");
        var resposta = await client.PostAsync($"/api/Lancamentos/{lancamento.Id}/Pagamento", conteudo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((StatusLancamento.Pendente, (DateOnly?)null, (decimal?)null), (salvo.Status, salvo.DataPagamento, salvo.ValorPago));
    }

    [Fact]
    public async Task Registrar_ValorPagoAcimaDoValorDoLancamento_Retorna400()
    {
        var (client, _, lancamento) = await CriarAsync(30m);

        var resposta = await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 30.01m, Ontem);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains("valorPago", await resposta.Content.ReadAsStringAsync());
        Assert.Null((await LerAsync(lancamento.Id)).ValorPago);
    }

    [Fact]
    public async Task Registrar_ConfirmacaoDuplicada_Retorna409_EPreservaOPrimeiroPagamento()
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem)).StatusCode);

        var duplicada = await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 40m, Ontem.AddDays(-1));

        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);
        Assert.Contains("/Pagamento", await duplicada.Content.ReadAsStringAsync());
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((Ontem, 50m), (salvo.DataPagamento!.Value, salvo.ValorPago!.Value));
    }

    [Fact]
    public async Task Atualizar_PagamentoRegistrado_CorrigeDataEValor_ERegistraQuemCorrigiu()
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem)).StatusCode);
        var (outro, outroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DIR");

        var resposta = await AtualizarPagamentoAsync(outro, lancamento.Id, new { dataPagamento = Ontem.AddDays(-3).ToString("yyyy-MM-dd"), valorPago = 45.5m });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((StatusLancamento.Pago, Ontem.AddDays(-3), 45.5m, outroId, outroId), (salvo.Status, salvo.DataPagamento!.Value, salvo.ValorPago!.Value, salvo.PagamentoRegistradoPorUsuarioId!.Value, salvo.AtualizadoPorUsuarioId!.Value));
    }

    [Fact]
    public async Task Atualizar_SemPagamentoRegistrado_Retorna409()
    {
        var (client, _, lancamento) = await CriarAsync();

        var resposta = await AtualizarPagamentoAsync(client, lancamento.Id, new { dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago = 10m });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Null((await LerAsync(lancamento.Id)).DataPagamento);
    }

    [Fact]
    public async Task Atualizar_ValorAcimaDoPrevisto_Retorna400_SemAlterar()
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem)).StatusCode);

        var resposta = await AtualizarPagamentoAsync(client, lancamento.Id, new { dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago = 60m });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(50m, (await LerAsync(lancamento.Id)).ValorPago);
    }

    [Fact]
    public async Task Registrar_IgnoraStatusEResponsavelEnviadosNoCorpo()
    {
        var (client, usuarioId, lancamento) = await CriarAsync(50m);
        var forjado = Guid.NewGuid();

        var resposta = await client.PostAsJsonAsync($"/api/Lancamentos/{lancamento.Id}/Pagamento", new
        {
            dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago = 50m, status = "Pendente", usuarioResponsavelId = forjado, id = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((StatusLancamento.Pago, usuarioId), (salvo.Status, salvo.PagamentoRegistradoPorUsuarioId!.Value));
    }

    [Fact]
    public async Task Registrar_LancamentoInexistenteOuExcluido_Retorna404()
    {
        var (client, _, lancamento) = await CriarAsync();
        var excluir = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{lancamento.Id}") { Content = JsonContent.Create(new { motivo = "teste" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(excluir)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TestHelpers.RegistrarPagamentoAsync(client, Guid.NewGuid(), 10m)).StatusCode);
        Assert.Null((await LerAsync(lancamento.Id)).DataPagamento);
    }

    [Fact]
    public async Task Registrar_LancamentoAtrasado_PassaAPago()
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/Lancamentos/{lancamento.Id}", new { status = "Atrasado" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem)).StatusCode);

        Assert.Equal(StatusLancamento.Pago, (await LerAsync(lancamento.Id)).Status);
    }

    [Fact]
    public async Task PutGenerico_ParaPago_Retorna409_OrientandoOEndpointDedicado()
    {
        var (client, _, lancamento) = await CriarAsync();

        var resposta = await client.PutAsJsonAsync($"/api/Lancamentos/{lancamento.Id}", new { status = "Pago" });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Contains("/api/Lancamentos/{id}/Pagamento", await resposta.Content.ReadAsStringAsync());
        Assert.Equal(StatusLancamento.Pendente, (await LerAsync(lancamento.Id)).Status);
    }

    [Theory]
    [InlineData("""{"status": "Pendente"}""")]
    [InlineData("""{"status": "Atrasado"}""")]
    [InlineData("""{"valor": 40}""")]
    [InlineData("""{"tipoFluxo": "Saida"}""")]
    public async Task PutGenerico_ComPagamentoRegistrado_NaoAlteraStatusValorNemFluxo(string corpo)
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem)).StatusCode);

        using var conteudo = new StringContent(corpo, System.Text.Encoding.UTF8, "application/json");
        var resposta = await client.PutAsync($"/api/Lancamentos/{lancamento.Id}", conteudo);

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((StatusLancamento.Pago, 50m, TipoFluxoLancamento.Entrada, 50m), (salvo.Status, salvo.Valor, salvo.TipoFluxo, salvo.ValorPago!.Value));
    }

    [Fact]
    public async Task PutGenerico_ComPagamentoRegistrado_AindaCorrigeDescricao()
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem)).StatusCode);

        var resposta = await client.PutAsJsonAsync($"/api/Lancamentos/{lancamento.Id}", new { descricao = "Outubro (corrigido)", status = "Pago" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal(("Outubro (corrigido)", StatusLancamento.Pago, 50m), (salvo.Descricao, salvo.Status, salvo.ValorPago!.Value));
    }

    [Fact]
    public async Task Historico_PagoSemDadosDePagamento_ContinuaValido_EPodeSerRegularizado()
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            var entity = await db.Lancamentos.SingleAsync(l => l.Id == lancamento.Id);
            entity.Status = StatusLancamento.Pago;
            return await db.SaveChangesAsync();
        });

        var historico = await client.GetFromJsonAsync<LancamentoDto>($"/api/Lancamentos/{lancamento.Id}", Json);
        Assert.Equal((StatusLancamento.Pago, (DateOnly?)null, (decimal?)null, (decimal?)null), (historico!.Status, historico.DataPagamento, historico.ValorPago, historico.ValorRestante));
        Assert.Equal(HttpStatusCode.Conflict, (await AtualizarPagamentoAsync(client, lancamento.Id, new { dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago = 50m })).StatusCode);

        var regularizado = await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 48m, Ontem.AddDays(-40));

        Assert.Equal(HttpStatusCode.OK, regularizado.StatusCode);
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((StatusLancamento.Pago, Ontem.AddDays(-40), 48m), (salvo.Status, salvo.DataPagamento!.Value, salvo.ValorPago!.Value));
    }

    [Fact]
    public async Task Pendente_ExibeValorRestanteIgualAoValor_ESemDadosDePagamento()
    {
        var (client, _, lancamento) = await CriarAsync(33.33m);

        var dto = await client.GetFromJsonAsync<LancamentoDto>($"/api/Lancamentos/{lancamento.Id}", Json);

        Assert.Equal((33.33m, (DateOnly?)null, (decimal?)null, (Guid?)null), (dto!.ValorRestante!.Value, dto.DataPagamento, dto.ValorPago, dto.PagamentoRegistradoPorUsuarioId));
    }

    [Fact]
    public async Task ExclusaoLogica_DeLancamentoPago_ContinuaPermitida()
    {
        var (client, _, lancamento) = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, lancamento.Id, 50m, Ontem)).StatusCode);

        var excluir = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{lancamento.Id}") { Content = JsonContent.Create(new { motivo = "estorno manual" }) };

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(excluir)).StatusCode);
        var salvo = await LerAsync(lancamento.Id);
        Assert.Equal((false, 50m), (salvo.Ativo, salvo.ValorPago!.Value));
        Assert.Equal(HttpStatusCode.NotFound, (await AtualizarPagamentoAsync(client, lancamento.Id, new { dataPagamento = Ontem.ToString("yyyy-MM-dd"), valorPago = 1m })).StatusCode);
    }

    [Fact]
    public async Task CargoSemPermissao_NaoRegistraPagamento()
    {
        var (_, _, lancamento) = await CriarAsync(50m);
        var (desbravador, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        using var anonimo = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await TestHelpers.RegistrarPagamentoAsync(desbravador, lancamento.Id, 50m)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.RegistrarPagamentoAsync(anonimo, lancamento.Id, 50m)).StatusCode);
        Assert.Null((await LerAsync(lancamento.Id)).DataPagamento);
    }
}
