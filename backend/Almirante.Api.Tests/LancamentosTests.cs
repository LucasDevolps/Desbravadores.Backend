using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

public class LancamentosTests
{
    [Fact]
    public async Task List_SemToken_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Lancamentos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_ComPaginacaoPadrao_RetornaEnvelopeComSeedDemonstrativo()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Lancamentos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentosResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body!.Page);
        Assert.Equal(10, body.PageSize);
        Assert.True(body.Total >= 20);
        Assert.True(body.TotalPages >= 1);
        Assert.Equal(10, body.Items.Count);
    }

    [Fact]
    public async Task List_FiltrandoPorTipoECategoria_RetornaApenasCorrespondentes()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Lancamentos?tipo=Campori&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentosResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Items);
        Assert.All(body.Items, item => Assert.Equal("Campori", item.Tipo));
    }

    [Fact]
    public async Task List_ComBuscaPorNomeDoMembro_FiltraResultados()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Lancamentos?search=guilherme&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentosResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Items);
        Assert.All(body.Items, item => Assert.Contains("guilherme", item.MembroNome.ToLower()));
    }

    [Fact]
    public async Task List_ComPageSizeMaiorQue100_LimitaA100()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Lancamentos?pageSize=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentosResponse>();
        Assert.Equal(100, body!.PageSize);
    }

    [Fact]
    public async Task Create_ComDadosValidos_Retorna201ESemMembroObrigatorio()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.PostAsJsonAsync("/api/Lancamentos", new
        {
            membroNome = "Novo Membro",
            tipo = "Doação",
            categoria = "Clube",
            valor = 42.50m,
            vencimento = "2026-12-01",
            status = "Pendente",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.NotNull(created);
        Assert.Null(created!.MembroId);
        Assert.Equal("Novo Membro", created.MembroNome);
        Assert.Equal("BRL", created.Moeda);
    }

    [Fact]
    public async Task Create_ComValorNegativo_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.PostAsJsonAsync("/api/Lancamentos", new
        {
            membroNome = "Membro",
            tipo = "Outros",
            categoria = "Clube",
            valor = -10m,
            vencimento = "2026-12-01",
            status = "Pendente",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FluxoCompleto_CriarAtualizarExcluir_FuncionaDeFimAFim()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var createResponse = await client.PostAsJsonAsync("/api/Lancamentos", new
        {
            membroNome = "Membro Fluxo",
            tipo = "Evento",
            categoria = "Evento",
            valor = 100m,
            vencimento = "2026-11-15",
            status = "Pendente",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<LancamentoDto>();

        var updateResponse = await client.PutAsJsonAsync($"/api/Lancamentos/{created!.Id}", new
        {
            status = "Pago",
            valor = 150m,
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.Equal("Pago", updated!.Status);
        Assert.Equal(150m, updated.Valor);
        Assert.Equal("Membro Fluxo", updated.MembroNome);

        var deleteResponse = await client.DeleteAsync($"/api/Lancamentos/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getAfterDelete = await client.GetAsync($"/api/Lancamentos?search=Membro Fluxo");
        var afterDeleteBody = await getAfterDelete.Content.ReadFromJsonAsync<LancamentosResponse>();
        Assert.DoesNotContain(afterDeleteBody!.Items, item => item.Id == created.Id);
    }

    [Fact]
    public async Task Update_RegistroInexistente_Retorna404()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{Guid.NewGuid()}", new { status = "Pago" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RegistroInexistente_Retorna404()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.DeleteAsync($"/api/Lancamentos/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
