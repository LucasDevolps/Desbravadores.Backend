using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class EventosItensAdicionaisSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private static readonly object[] InscricaoEMaterial = [new { titulo = "Inscrição", valor = 30m }, new { titulo = "Material", valor = 12.50m }];

    private Task<List<Guid>> MembrosAsync(int quantidade) => EventosSqlSupport.MembrosAsync(Factory, quantidade);

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private static Dictionary<string, object?> CorpoCriacao(List<Guid> membros, object? itens, decimal transporte = 100m)
    {
        var corpo = EventosTestKit.Corpo(membros, transporte: transporte, alimentacao: 0m, seguro: 0m);
        if (itens is not null) corpo["itensAdicionais"] = itens;
        return corpo;
    }

    private async Task<EventoDto> CriarAsync(List<Guid> membros, object? itens, decimal transporte = 100m)
    {
        var resposta = await EventosTestKit.PostAsync(Client, CorpoCriacao(membros, itens, transporte));
        await Esperar(HttpStatusCode.Created, resposta);
        return await EventosTestKit.LerAsync(resposta);
    }

    private async Task<EventoDto> ObterAsync(Guid id) => (await Client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{id}", Json))!;

    private static Dictionary<string, object?> CorpoEdicao(EventoDto dto, List<Guid> membros, object? itens, bool informarItens = true,
        decimal? transporte = null, string? motivo = null)
    {
        var corpo = EventosSqlSupport.Editar(dto, membros, transporte: transporte, motivo: motivo);
        if (informarItens) corpo["itensAdicionais"] = itens;
        return corpo;
    }

    private async Task<EventoDto> EditarOkAsync(EventoDto dto, List<Guid> membros, object? itens, bool informarItens = true, decimal? transporte = null,
        string? motivo = null)
    {
        var resposta = await EventosTestKit.PutAsync(Client, dto.Id, CorpoEdicao(dto, membros, itens, informarItens, transporte, motivo));
        await Esperar(HttpStatusCode.OK, resposta);
        return await EventosTestKit.LerAsync(resposta);
    }

    private static async Task Esperar(HttpStatusCode esperado, HttpResponseMessage resposta) =>
        Assert.True(resposta.StatusCode == esperado, $"{(int)resposta.StatusCode}: {await resposta.Content.ReadAsStringAsync()}");

    private static IEnumerable<(string, decimal)> Itens(EventoDto dto) => dto.ItensAdicionais.Select(i => (i.Titulo, i.Valor));

    [Fact]
    public async Task Base100ComInscricaoEMaterial_142e50PorParticipanteE285Estimados()
    {
        var m = await MembrosAsync(2);

        var dto = await CriarAsync(m, InscricaoEMaterial);

        Assert.Equal((142.50m, 285m, 285m), (dto.ValorPorMembro, dto.TotalEstimado, dto.Total));
        Assert.All(dto.Lancamentos, l => Assert.Equal(142.50m, l.Valor));
        Assert.Equal([("Inscrição", 30m), ("Material", 12.50m)], Itens(await ObterAsync(dto.Id)));
        var gravado = await Db(db => db.Eventos.AsNoTracking().SingleAsync(e => e.Id == dto.Id));
        Assert.Equal((42.50m, 142.50m), (gravado.ItensAdicionaisValor, gravado.ValorPorMembro));
    }

    [Fact]
    public async Task RenomearItem_MantemTotal_EMudarValorRecalculaUmaVez()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, InscricaoEMaterial);

        var renomeado = await EditarOkAsync(dto, m, new object[] { new { titulo = "Taxa de inscrição", valor = 30m }, new { titulo = "Material", valor = 12.50m } });
        Assert.Equal((142.50m, 285m), (renomeado.ValorPorMembro, renomeado.TotalEstimado));
        Assert.Equal("Taxa de inscrição", renomeado.ItensAdicionais[0].Titulo);

        var alterado = await EditarOkAsync(renomeado, m, new object[] { new { titulo = "Taxa de inscrição", valor = 40m }, new { titulo = "Material", valor = 12.50m } });
        Assert.Equal((152.50m, 305m), (alterado.ValorPorMembro, alterado.TotalEstimado));
        Assert.All(alterado.Lancamentos, l => Assert.Equal(152.50m, l.Valor));

        var removido = await EditarOkAsync(alterado, m, new object[] { new { titulo = "Material", valor = 12.50m } });
        Assert.Equal((112.50m, 225m), (removido.ValorPorMembro, removido.TotalEstimado));
        Assert.Equal([("Material", 12.50m)], Itens(await ObterAsync(dto.Id)));
        Assert.Equal(1, await Db(db => db.EventosItensAdicionais.CountAsync(i => i.EventoId == dto.Id && i.Ativo)));
    }

    [Fact]
    public async Task ItensOmitidosNoPut_PreservaOsExistentes_EListaVaziaRemoveTodos()
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, InscricaoEMaterial);

        var preservado = await EditarOkAsync(dto, m, null, informarItens: false, transporte: 110m);
        Assert.Equal((152.50m, 2), (preservado.ValorPorMembro, preservado.ItensAdicionais.Count));

        var semItens = await EditarOkAsync(preservado, m, Array.Empty<object>());
        Assert.Equal((110m, 0), (semItens.ValorPorMembro, semItens.ItensAdicionais.Count));
        Assert.Equal(0m, await Db(db => db.Eventos.AsNoTracking().Where(e => e.Id == dto.Id).Select(e => e.ItensAdicionaisValor).SingleAsync()));
    }

    [Fact]
    public async Task CadastroLegadoSemItens_RetornaListaVazia_EAceitaItensNaEdicao()
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, null);
        Assert.Empty((await ObterAsync(dto.Id)).ItensAdicionais);

        var editado = await EditarOkAsync(dto, m, new object[] { new { titulo = "Inscrição", valor = 30m } });

        Assert.Equal((130m, 130m), (editado.ValorPorMembro, editado.Lancamentos.Single().Valor));
    }

    [Fact]
    public async Task ItemInvalido_NaoSalvaNadaEIdentificaOItem()
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, InscricaoEMaterial);

        var resposta = await EventosTestKit.PutAsync(Client, dto.Id,
            CorpoEdicao(dto, m, new object[] { new { titulo = "Inscrição", valor = 50m }, new { titulo = "Material", valor = 0m } }, transporte: 200m));

        await Esperar(HttpStatusCode.BadRequest, resposta);
        Assert.Contains("ItensAdicionais[1].Valor", await resposta.Content.ReadAsStringAsync());
        var atual = await ObterAsync(dto.Id);
        Assert.Equal((142.50m, dto.Versao), (atual.ValorPorMembro, atual.Versao));
        Assert.Equal([("Inscrição", 30m), ("Material", 12.50m)], Itens(atual));

        var criacao = await EventosTestKit.PostAsync(Client, CorpoCriacao(m, new object[] { new { titulo = " ", valor = 5m } }));
        await Esperar(HttpStatusCode.BadRequest, criacao);
        Assert.Contains("ItensAdicionais[0].Titulo", await criacao.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReenvioIdempotente_ComItens_DevolveOOriginal_EItensDiferentesDao409()
    {
        var m = await MembrosAsync(1);
        var chave = Guid.NewGuid().ToString();
        var primeira = await EventosTestKit.PostAsync(Client, CorpoCriacao(m, InscricaoEMaterial), chave);
        await Esperar(HttpStatusCode.Created, primeira);
        var original = await EventosTestKit.LerAsync(primeira);

        var reenvio = await EventosTestKit.PostAsync(Client, CorpoCriacao(m, InscricaoEMaterial), chave);
        await Esperar(HttpStatusCode.OK, reenvio);
        Assert.Equal((original.Id, 2), ((await EventosTestKit.LerAsync(reenvio)).Id, (await EventosTestKit.LerAsync(reenvio)).ItensAdicionais.Count));

        var diferente = await EventosTestKit.PostAsync(Client, CorpoCriacao(m, new object[] { new { titulo = "Inscrição", valor = 31m } }), chave);
        await Esperar(HttpStatusCode.Conflict, diferente);
        Assert.Equal(1, await Db(db => db.Eventos.CountAsync(e => e.Id == original.Id)));
    }

    [Fact]
    public async Task VersaoDesatualizada_Retorna409SemMudarItens()
    {
        var m = await MembrosAsync(1);
        var dto = await CriarAsync(m, InscricaoEMaterial);
        await EditarOkAsync(dto, m, null, informarItens: false, transporte: 120m);

        var resposta = await EventosTestKit.PutAsync(Client, dto.Id, CorpoEdicao(dto, m, Array.Empty<object>()));

        await Esperar(HttpStatusCode.Conflict, resposta);
        Assert.Equal(2, (await ObterAsync(dto.Id)).ItensAdicionais.Count);
    }

    [Fact]
    public async Task ComPagamento_RenomearPassa_AumentarDa409_EReduzirGeraDevolucao()
    {
        var m = await MembrosAsync(2);
        var dto = await CriarAsync(m, InscricaoEMaterial);
        await Esperar(HttpStatusCode.OK, await TestHelpers.PagarAsync(Client, dto.Lancamentos.Single(l => l.MembroId == m[0]).Id));
        dto = await ObterAsync(dto.Id);

        var renomeado = await EditarOkAsync(dto, m, new object[] { new { titulo = "Inscrição oficial", valor = 30m }, new { titulo = "Material", valor = 12.50m } });

        var aumento = await EventosTestKit.PutAsync(Client, dto.Id,
            CorpoEdicao(renomeado, m, new object[] { new { titulo = "Inscrição oficial", valor = 35m }, new { titulo = "Material", valor = 12.50m } }));
        await Esperar(HttpStatusCode.Conflict, aumento);

        var trocaDeComposicao = await EventosTestKit.PutAsync(Client, dto.Id,
            CorpoEdicao(renomeado, m, new object[] { new { titulo = "Inscrição oficial", valor = 42.50m } }));
        await Esperar(HttpStatusCode.Conflict, trocaDeComposicao);

        var reduzido = await EditarOkAsync(renomeado, m, new object[] { new { titulo = "Inscrição oficial", valor = 30m } }, motivo: "material doado");

        Assert.Equal(130m, reduzido.ValorPorMembro);
        var devolucao = Assert.Single(reduzido.Devolucoes);
        Assert.Equal((m[0], 12.50m), (devolucao.MembroId, devolucao.Valor));
        Assert.Equal(130m, reduzido.Lancamentos.Single(l => l.MembroId == m[1]).Valor);
        Assert.Equal((260m, 142.50m, 130m), (reduzido.TotalEstimado, reduzido.TotalPago, reduzido.ValorAReceber));
    }

    [Fact]
    public async Task ParticipanteComAjusteOuIsencao_PreservadoQuandoOsItensMudam()
    {
        var m = await MembrosAsync(3);
        var dto = await CriarAsync(m, InscricaoEMaterial);
        var ajuste = await Client.PutAsJsonAsync($"/api/Lancamentos/{dto.Lancamentos.Single(l => l.MembroId == m[0]).Id}", new { valor = 100m });
        await Esperar(HttpStatusCode.OK, ajuste);
        var isencao = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{dto.Lancamentos.Single(l => l.MembroId == m[1]).Id}")
        {
            Content = JsonContent.Create(new { motivo = "bolsa integral" }),
        };
        await Esperar(HttpStatusCode.NoContent, await Client.SendAsync(isencao));

        var editado = await EditarOkAsync(await ObterAsync(dto.Id), m, new object[] { new { titulo = "Inscrição", valor = 50m } });

        Assert.Equal(150m, editado.ValorPorMembro);
        Assert.Equal(100m, editado.Lancamentos.Single(l => l.MembroId == m[0]).Valor);
        Assert.DoesNotContain(editado.Lancamentos, l => l.MembroId == m[1]);
        Assert.Equal(150m, editado.Lancamentos.Single(l => l.MembroId == m[2]).Valor);
        Assert.Equal(250m, editado.TotalEstimado);
    }

    [Fact]
    public async Task ParticipanteVeOsItensEmMeEventos()
    {
        var m = await MembrosAsync(1);
        var membro = await Db(db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == m[0]));
        var dto = await CriarAsync(m, InscricaoEMaterial);
        var client = Factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var login = await client.PostAsJsonAsync("/api/Auth/login", new { email = membro.Email, senha = TestHelpers.SenhaPadraoTeste });
        await Esperar(HttpStatusCode.OK, login);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var meu = (await client.GetFromJsonAsync<EventoParticipanteDto>($"/api/Me/Eventos/{dto.Id}", Json))!;

        Assert.Equal([("Inscrição", 30m), ("Material", 12.50m)], meu.ItensAdicionais.Select(i => (i.Titulo, i.Valor)));
        Assert.Equal(142.50m, meu.ValorPorMembro);
    }
}
