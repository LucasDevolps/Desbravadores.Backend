using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class EventosCobrancasIndividuaisSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private Task<List<Guid>> MembrosAsync(int quantidade) => EventosSqlSupport.MembrosAsync(Factory, quantidade);

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private async Task<EventoDto> CriarAsync(List<Guid> membros, decimal valor = 252m, Guid? referencia = null)
    {
        var resposta = await EventosTestKit.PostAsync(Client,
            EventosTestKit.Corpo(membros, transporte: valor, alimentacao: 0m, seguro: 0m, referencia: referencia));
        Assert.True(resposta.StatusCode == HttpStatusCode.Created, await resposta.Content.ReadAsStringAsync());
        return await EventosTestKit.LerAsync(resposta);
    }

    private async Task<EventoDto> ObterAsync(Guid id) => (await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{id}", Json))!;

    private static Guid CobrancaDe(EventoDto dto, Guid membro) => dto.Lancamentos.Single(l => l.MembroId == membro).Id;

    private static Task<HttpResponseMessage> AjustarAsync(HttpClient client, Guid lancamentoId, decimal valor) =>
        client.PutAsJsonAsync($"/api/Lancamentos/{lancamentoId}", new { valor });

    private static Task<HttpResponseMessage> IsentarAsync(HttpClient client, Guid lancamentoId, string motivo = "desconto concedido pela diretoria") =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{lancamentoId}") { Content = JsonContent.Create(new { motivo }) });

    private async Task<EventoDto> EditarOkAsync(EventoDto dto, IEnumerable<Guid> membros, decimal? transporte = null, string? local = null,
        string? motivo = null)
    {
        var resposta = await EventosTestKit.PutAsync(Client, dto.Id,
            EventosSqlSupport.Editar(dto, membros.ToList(), transporte: transporte, local: local, motivo: motivo));
        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return await EventosTestKit.LerAsync(resposta);
    }

    private static async Task Esperar(HttpStatusCode esperado, HttpResponseMessage resposta) =>
        Assert.True(resposta.StatusCode == esperado, $"{(int)resposta.StatusCode}: {await resposta.Content.ReadAsStringAsync()}");

    private static async Task<string?> CodigoAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        return corpo.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;
    }

    [Fact]
    public async Task AjusteDaAnaPara200_SoMudaACobrancaDelaEEstima2468()
    {
        var m = await MembrosAsync(10);
        var dto = await CriarAsync(m);
        var ana = m[0];

        var resposta = await AjustarAsync(Client, CobrancaDe(dto, ana), 200m);

        await Esperar(HttpStatusCode.OK, resposta);
        var lancamento = (await resposta.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        Assert.Equal((200m, true, StatusLancamento.Pendente), (lancamento.Valor, lancamento.ValorAjustado, lancamento.Status));

        var evento = await ObterAsync(dto.Id);
        Assert.Equal(252m, evento.ValorPorMembro);
        Assert.Equal((2468m, 2468m, 0m, 2468m, 0m),
            (evento.TotalEstimado, evento.Total, evento.TotalPago, evento.ValorAReceber, evento.TotalDevolvido));
        Assert.All(evento.Lancamentos.Where(l => l.MembroId != ana), l => Assert.Equal((252m, false), (l.Valor, l.ValorAjustado)));
        Assert.Equal((200m, true), evento.Lancamentos.Where(l => l.MembroId == ana).Select(l => (l.Valor, l.ValorAjustado)).Single());
    }

    [Fact]
    public async Task PagamentoParcialDe80_Pago80EAReceber2388()
    {
        var m = await MembrosAsync(10);
        var dto = await CriarAsync(m);
        await Esperar(HttpStatusCode.OK, await AjustarAsync(Client, CobrancaDe(dto, m[0]), 200m));

        await Esperar(HttpStatusCode.OK, await TestHelpers.RegistrarPagamentoAsync(Client, CobrancaDe(dto, m[0]), 80m));

        var evento = await ObterAsync(dto.Id);
        Assert.Equal((2468m, 80m, 2388m), (evento.TotalEstimado, evento.TotalPago, evento.ValorAReceber));
        Assert.Equal(80m, evento.Lancamentos.Single(l => l.MembroId == m[0]).ValorPago);
    }

    [Fact]
    public async Task IsencaoDaAnaSemPagamento_MantemDezInscritosNoveCobrancasE2268_ComAuditoria()
    {
        var m = await MembrosAsync(10);
        var dto = await CriarAsync(m);
        var cobrancaAna = CobrancaDe(dto, m[0]);

        await Esperar(HttpStatusCode.NoContent, await IsentarAsync(Client, cobrancaAna, "isenção por bolsa"));

        var evento = await ObterAsync(dto.Id);
        Assert.Equal((10, 9, 2268m, 2268m), (evento.QuantidadeMembros, evento.Lancamentos.Count, evento.TotalEstimado, evento.ValorAReceber));
        Assert.Contains(m[0], evento.Membros);
        var isencao = Assert.Single(evento.Isencoes);
        Assert.Equal((m[0], cobrancaAna, "isenção por bolsa", fx.AdminId), (isencao.MembroId, isencao.LancamentoId, isencao.Motivo, isencao.IsentoPorUsuarioId));

        var participante = await Db(db => db.EventosMembros.AsNoTracking().SingleAsync(p => p.EventoId == dto.Id && p.MembroId == m[0]));
        Assert.Equal((true, true, (Guid?)null, (Guid?)cobrancaAna), (participante.Ativo, participante.Isento, participante.LancamentoId, participante.IsencaoLancamentoId));
        var auditoria = await Db(db => db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == cobrancaAna));
        Assert.Equal(("isenção por bolsa", fx.AdminId, 252m), (auditoria.Motivo, auditoria.UsuarioResponsavelId, auditoria.Valor));
        Assert.False(await Db(db => db.Lancamentos.AsNoTracking().Where(l => l.Id == cobrancaAna).Select(l => l.Ativo).SingleAsync()));
    }

    [Fact]
    public async Task EdicoesPosterioresDoEvento_PreservamIsencaoEAjuste()
    {
        var m = await MembrosAsync(10);
        var dto = await CriarAsync(m);
        await Esperar(HttpStatusCode.NoContent, await IsentarAsync(Client, CobrancaDe(dto, m[0])));
        await Esperar(HttpStatusCode.OK, await AjustarAsync(Client, CobrancaDe(dto, m[1]), 200m));

        var editado = await EditarOkAsync(await ObterAsync(dto.Id), m, local: "Parque Villa-Lobos");
        Assert.Equal((9, 252m * 8 + 200m), (editado.Lancamentos.Count, editado.TotalEstimado));

        editado = await EditarOkAsync(editado, m, transporte: 300m);

        Assert.Equal(300m, editado.ValorPorMembro);
        Assert.DoesNotContain(editado.Lancamentos, l => l.MembroId == m[0]);
        Assert.Equal(200m, editado.Lancamentos.Single(l => l.MembroId == m[1]).Valor);
        Assert.All(editado.Lancamentos.Where(l => l.MembroId != m[1]), l => Assert.Equal(300m, l.Valor));
        Assert.Equal(300m * 8 + 200m, (await ObterAsync(dto.Id)).TotalEstimado);
        Assert.Single((await ObterAsync(dto.Id)).Isencoes);
    }

    [Fact]
    public async Task EventoZeradoEDepoisComValor_NaoRecriaCobrancaDoIsento()
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(m, 50m);
        await Esperar(HttpStatusCode.NoContent, await IsentarAsync(Client, CobrancaDe(dto, m[0])));

        var zerado = await EditarOkAsync(await ObterAsync(dto.Id), m, transporte: 0m, motivo: "evento passou a ser gratuito");
        Assert.Empty(zerado.Lancamentos);

        var comValor = await EditarOkAsync(zerado, m, transporte: 40m);

        Assert.Equal(2, comValor.Lancamentos.Count);
        Assert.DoesNotContain(comValor.Lancamentos, l => l.MembroId == m[0]);
        Assert.Equal(80m, comValor.TotalEstimado);
    }

    [Fact]
    public async Task AjusteDeVoltaAoValorPadrao_VoltaAAcompanharOEvento()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, 100m);
        var cobranca = CobrancaDe(dto, m[0]);
        await Esperar(HttpStatusCode.OK, await AjustarAsync(Client, cobranca, 70m));

        var resposta = await AjustarAsync(Client, cobranca, 100m);
        await Esperar(HttpStatusCode.OK, resposta);
        Assert.False((await resposta.Content.ReadFromJsonAsync<LancamentoDto>(Json))!.ValorAjustado);

        var editado = await EditarOkAsync(await ObterAsync(dto.Id), m, transporte: 120m);
        Assert.All(editado.Lancamentos, l => Assert.Equal(120m, l.Valor));
    }

    [Fact]
    public async Task ReducaoDoEventoComPago_NaoGeraDevolucaoParaCobrancaAjustada()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, 252m);
        await Esperar(HttpStatusCode.OK, await AjustarAsync(Client, CobrancaDe(dto, m[0]), 200m));
        await Esperar(HttpStatusCode.OK, await TestHelpers.RegistrarPagamentoAsync(Client, CobrancaDe(dto, m[0]), 200m));

        var reduzido = await EditarOkAsync(await ObterAsync(dto.Id), m, transporte: 150m, motivo: "patrocínio");

        Assert.Empty(reduzido.Devolucoes);
        Assert.Equal((200m, 150m), (reduzido.Lancamentos.Single(l => l.MembroId == m[0]).Valor, reduzido.Lancamentos.Single(l => l.MembroId == m[1]).Valor));
        Assert.Equal((350m, 200m, 150m), (reduzido.TotalEstimado, reduzido.TotalPago, reduzido.ValorAReceber));
    }

    [Fact]
    public async Task CobrancaComPagamentoParcial_RecusaAjusteEIsencaoSemTocarNoPagamento()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, 100m);
        var cobranca = CobrancaDe(dto, m[0]);
        await Esperar(HttpStatusCode.OK, await TestHelpers.RegistrarPagamentoAsync(Client, cobranca, 30m));

        var ajuste = await AjustarAsync(Client, cobranca, 60m);
        var isencao = await IsentarAsync(Client, cobranca);

        await Esperar(HttpStatusCode.Conflict, ajuste);
        await Esperar(HttpStatusCode.Conflict, isencao);
        Assert.Equal(LancamentosService.CodigoCobrancaComPagamento, await CodigoAsync(ajuste));
        Assert.Equal(LancamentosService.CodigoCobrancaComPagamento, await CodigoAsync(isencao));
        var salvo = await Db(db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == cobranca));
        Assert.Equal((true, 100m, 30m, StatusLancamento.Pago), (salvo.Ativo, salvo.Valor, salvo.ValorPago!.Value, salvo.Status));
        Assert.Empty((await ObterAsync(dto.Id)).Isencoes);
    }

    [Fact]
    public async Task Devolucao_NaoAceitaAjusteNemIsencao()
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, 100m);
        await Esperar(HttpStatusCode.OK, await TestHelpers.PagarAsync(Client, CobrancaDe(dto, m[0])));
        var reduzido = await EditarOkAsync(await ObterAsync(dto.Id), m, transporte: 60m, motivo: "redução");
        var devolucao = Assert.Single(reduzido.Devolucoes).Id;

        await Esperar(HttpStatusCode.Conflict, await AjustarAsync(Client, devolucao, 10m));
        await Esperar(HttpStatusCode.Conflict, await IsentarAsync(Client, devolucao));
        Assert.True(await Db(db => db.Lancamentos.AsNoTracking().Where(l => l.Id == devolucao).Select(l => l.Ativo).SingleAsync()));
    }

    private async Task AjusteRecusadoComValor(decimal valor)
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, 100m);

        await Esperar(HttpStatusCode.BadRequest, await AjustarAsync(Client, CobrancaDe(dto, m[0]), valor));

        Assert.Equal(100m, (await ObterAsync(dto.Id)).Lancamentos.Single().Valor);
    }

    [Fact]
    public Task AjusteParaZero_Retorna400() => AjusteRecusadoComValor(0m);

    [Fact]
    public Task AjusteComTresCasasDecimais_Retorna400() => AjusteRecusadoComValor(10.005m);

    [Fact]
    public async Task AjusteDeMembroOuVencimento_ContinuaRecusado()
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, 100m);

        var resposta = await Client.PutAsJsonAsync($"/api/Lancamentos/{CobrancaDe(dto, m[0])}",
            new { valor = 80m, vencimento = EventosTestKit.Hoje.AddDays(40).ToString("yyyy-MM-dd") });

        await Esperar(HttpStatusCode.Conflict, resposta);
        Assert.Equal(100m, (await ObterAsync(dto.Id)).Lancamentos.Single().Valor);
    }

    [Fact]
    public async Task CadastrosDoMesmoGrupo_TemTotaisIndependentes()
    {
        var m = await MembrosAsync(4);
        var a = await CriarAsync(m.Take(2).ToList(), 100m);
        var b = await CriarAsync(m.Skip(2).ToList(), 60m, referencia: a.Id);

        await Esperar(HttpStatusCode.OK, await AjustarAsync(Client, CobrancaDe(a, m[0]), 50m));
        await Esperar(HttpStatusCode.NoContent, await IsentarAsync(Client, CobrancaDe(b, m[2])));

        var (atualA, atualB) = (await ObterAsync(a.Id), await ObterAsync(b.Id));
        Assert.Equal(a.EventoGrupoId, atualB.EventoGrupoId);
        Assert.Equal((150m, 60m), (atualA.TotalEstimado, atualB.TotalEstimado));
        Assert.Empty(atualA.Isencoes);
        Assert.Single(atualB.Isencoes);
    }

    [Fact]
    public async Task EventoGratuito_TotaisZerados()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, 0m);

        Assert.Equal((0m, 0m, 0m, 0m), (dto.TotalEstimado, dto.TotalPago, dto.ValorAReceber, dto.TotalDevolvido));
        Assert.Empty(dto.Lancamentos);
    }

    [Fact]
    public async Task ExclusaoDoEventoComIsencao_FuncionaENaoReativaNada()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, 100m);
        await Esperar(HttpStatusCode.NoContent, await IsentarAsync(Client, CobrancaDe(dto, m[0])));
        var atual = await ObterAsync(dto.Id);

        await Esperar(HttpStatusCode.NoContent, await EventosTestKit.DeleteAsync(Client, dto.Id, "evento cancelado", atual.Versao));

        Assert.False(await Db(db => db.Lancamentos.AsNoTracking().AnyAsync(l => l.EventoId == dto.Id && l.Ativo)));
        await Esperar(HttpStatusCode.NotFound, await IsentarAsync(Client, CobrancaDe(dto, m[1])));
    }

    [Fact]
    public async Task VersaoDoEventoMuda_AposAjusteOuIsencao()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, 100m);

        await Esperar(HttpStatusCode.OK, await AjustarAsync(Client, CobrancaDe(dto, m[0]), 80m));

        var resposta = await EventosTestKit.PutAsync(Client, dto.Id, EventosSqlSupport.Editar(dto, m, local: "Outro lugar"));
        await Esperar(HttpStatusCode.Conflict, resposta);
    }

    [Fact]
    public async Task PagamentoEIsencaoConcorrentes_SoUmVenceENadaSePerde()
    {
        var m = await MembrosAsync(6);
        var dto = await CriarAsync(m, 100m);

        foreach (var membro in m)
        {
            var cobranca = CobrancaDe(dto, membro);
            var pagamento = TestHelpers.RegistrarPagamentoAsync(Client, cobranca, 100m);
            var isencao = IsentarAsync(Client, cobranca);
            var (pago, isento) = (await pagamento, await isencao);

            var salvo = await Db(db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == cobranca));
            var participante = await Db(db => db.EventosMembros.AsNoTracking().SingleAsync(p => p.EventoId == dto.Id && p.MembroId == membro));
            if (pago.StatusCode == HttpStatusCode.OK)
            {
                await Esperar(HttpStatusCode.Conflict, isento);
                Assert.Equal((true, StatusLancamento.Pago, false, (Guid?)cobranca), (salvo.Ativo, salvo.Status, participante.Isento, participante.LancamentoId));
            }
            else
            {
                await Esperar(HttpStatusCode.NoContent, isento);
                await Esperar(HttpStatusCode.NotFound, pago);
                Assert.Equal((false, (decimal?)null, true), (salvo.Ativo, salvo.ValorPago, participante.Isento));
            }
        }
    }

    private async Task CargoAjustaEIsenta(string role)
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, 100m);
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, role);

        await Esperar(HttpStatusCode.OK, await AjustarAsync(client, CobrancaDe(dto, m[0]), 90m));
        await Esperar(HttpStatusCode.NoContent, await IsentarAsync(client, CobrancaDe(dto, m[1])));
        await Esperar(HttpStatusCode.OK, await client.GetAsync($"/api/Lancamentos/{CobrancaDe(dto, m[0])}"));
    }

    private async Task CargoSemAcessoATesouraria(string role)
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, 100m);
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, role);

        await Esperar(HttpStatusCode.Forbidden, await AjustarAsync(client, CobrancaDe(dto, m[0]), 90m));
        await Esperar(HttpStatusCode.Forbidden, await IsentarAsync(client, CobrancaDe(dto, m[0])));
        await Esperar(HttpStatusCode.Forbidden, await client.GetAsync("/api/Lancamentos"));
        Assert.Equal(100m, (await ObterAsync(dto.Id)).Lancamentos.Single().Valor);
    }

    [Fact]
    public Task Administrador_AjustaEIsentaCobrancas() => CargoAjustaEIsenta(Roles.Admin);

    [Fact]
    public Task Diretor_AjustaEIsentaCobrancas() => CargoAjustaEIsenta(Roles.Diretor);

    [Fact]
    public Task DiretorAssociado_AjustaEIsentaCobrancas() => CargoAjustaEIsenta(Roles.DiretorAssociado);

    [Fact]
    public Task Tesoureiro_AjustaEIsentaCobrancas() => CargoAjustaEIsenta(Roles.Tesoureiro);

    [Fact]
    public Task Secretario_NaoAcessaATesouraria() => CargoSemAcessoATesouraria(Roles.Secretario);

    [Fact]
    public Task Desbravador_NaoAcessaATesouraria() => CargoSemAcessoATesouraria(Roles.Desbravador);
}
