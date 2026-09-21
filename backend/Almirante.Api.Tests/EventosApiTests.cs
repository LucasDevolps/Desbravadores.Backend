using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

// Contrato HTTP de /api/Eventos que não depende de banco relacional: autenticação, autorização, validação de
// entrada (formatos de "membros"), GET com período vazio e Swagger. Fluxos com persistência, triggers,
// rowversion e concorrência estão em EventosSqlServerTests (SQL Server real).
public sealed class EventosApiTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static readonly Guid A = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private async Task<HttpClient> ClienteAsync() => await TestHelpers.CreateAuthenticatedClientAsync(factory);

    private static async Task<JsonObject> ProblemaAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    // ------------------------------------------------------------------ POST: formatos de membros

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("\"nao-e-guid\"")]
    [InlineData("[]")]
    [InlineData("[\"00000000-0000-0000-0000-000000000000\"]")]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("[\"11111111-1111-4111-8111-111111111111\",\"11111111-1111-4111-8111-111111111111\"]")]
    [InlineData("[1]")]
    [InlineData("[\"nao-e-guid\"]")]
    public async Task Post_MembrosInvalidos_Retorna400(string membros)
    {
        using var client = await ClienteAsync();
        var response = await EventosTestKit.PostJsonBrutoAsync(client, $$"""
            {"dataEvento":"{{EventosTestKit.Hoje.AddDays(3):yyyy-MM-dd}}","local":"Parque","transporte":{"valor":10,"ehGratis":false},
             "alimentacao":{"individual":false,"valor":8},"seguroObrigatorio":2,"membros":{{membros}}}
            """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problema = await ProblemaAsync(response);
        Assert.Equal(400, (int)problema["status"]!);
    }

    [Fact]
    public async Task Post_MembrosAusente_Retorna400()
    {
        using var client = await ClienteAsync();
        var corpo = EventosTestKit.Corpo(A);
        corpo.Remove("membros");
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PostAsync(client, corpo)).StatusCode);
    }

    [Fact]
    public async Task Post_DuplicidadeNaoEhRemovidaSilenciosamente_ErroCitaOsIds()
    {
        using var client = await ClienteAsync();
        var response = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(new[] { A, B, A }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(A.ToString(), await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ POST: idempotency-key e campos

    [Fact]
    public async Task Post_SemIdempotencyKey_Retorna400()
    {
        using var client = await ClienteAsync();
        var response = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(A), semChave: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Idempotency-Key", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData(" ")]
    public async Task Post_IdempotencyKeyInvalida_Retorna400(string chave)
    {
        using var client = await ClienteAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(A), chave)).StatusCode);
    }

    [Fact]
    public async Task Post_DataAnteriorAoPrimeiroDiaDoMes_Retorna400()
    {
        using var client = await ClienteAsync();
        var anterior = EventosTestKit.PrimeiroDiaDoMes.AddDays(-1);
        var response = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(A, data: anterior));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(EventosTestKit.PrimeiroDiaDoMes.ToString("yyyy-MM-dd"), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_CamposObrigatoriosAusentes_Retorna400PorCampo()
    {
        using var client = await ClienteAsync();
        var response = await EventosTestKit.PostJsonBrutoAsync(client, "{}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var erros = (await ProblemaAsync(response))["errors"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("DataEvento", erros);
        Assert.Contains("Local", erros);
        Assert.Contains("Transporte", erros);
        Assert.Contains("Alimentacao", erros);
        Assert.Contains("SeguroObrigatorio", erros);
        Assert.Contains("Membros", erros);
    }

    [Fact]
    public async Task Post_ClienteNaoDefineCamposDoServidor()
    {
        // status, categoria, finalidade, ativo, total, eventoId, responsável e IP enviados no JSON são ignorados;
        // aqui só a validação é exercitada (sem banco): o corpo com esses campos continua sendo aceito pelo binder.
        using var client = await ClienteAsync();
        var corpo = EventosTestKit.Corpo(A, data: EventosTestKit.PrimeiroDiaDoMes.AddDays(-1));
        corpo["status"] = "Pago";
        corpo["total"] = 1m;
        corpo["ativo"] = false;
        var response = await EventosTestKit.PostAsync(client, corpo);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // só pela data
        var erros = (await ProblemaAsync(response))["errors"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Equal(["DataEvento"], erros);
    }

    // ------------------------------------------------------------------ PUT / DELETE: validação

    [Fact]
    public async Task Put_SemVersaoOuComMembrosInvalidos_Retorna400()
    {
        using var client = await ClienteAsync();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PutAsync(client, id, EventosTestKit.Corpo(A))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PutAsync(client, id, EventosTestKit.Corpo(Array.Empty<Guid>(), versao: "AAAAAAAAB9E="))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PutAsync(client, id, EventosTestKit.Corpo(null, versao: "AAAAAAAAB9E="))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PutAsync(client, id, EventosTestKit.Corpo("x", versao: "AAAAAAAAB9E="))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.PutAsync(client, id, EventosTestKit.Corpo(new[] { A, A }, versao: "AAAAAAAAB9E="))).StatusCode);
    }

    [Theory]
    [InlineData(null, "AAAAAAAAB9E=")]
    [InlineData("", "AAAAAAAAB9E=")]
    [InlineData("   ", "AAAAAAAAB9E=")]
    [InlineData("motivo", null)]
    [InlineData("motivo", "invalida")]
    public async Task Delete_MotivoOuVersaoInvalidos_Retorna400(string? motivo, string? versao)
    {
        using var client = await ClienteAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await EventosTestKit.DeleteAsync(client, Guid.NewGuid(), motivo, versao)).StatusCode);
    }

    [Fact]
    public async Task Delete_MotivoAcimaDe255_Retorna400()
    {
        using var client = await ClienteAsync();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await EventosTestKit.DeleteAsync(client, Guid.NewGuid(), new string('m', 256), "AAAAAAAAB9E=")).StatusCode);
    }

    // ------------------------------------------------------------------ GET

    [Fact]
    public async Task Get_SemParametros_UsaPeriodoPadraoEDevolveListaVazia_Nunca404()
    {
        using var client = await ClienteAsync();
        var response = await client.GetAsync("/api/Eventos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corpo = await response.Content.ReadFromJsonAsync<EventosResponse>();
        var primeiro = EventosTestKit.PrimeiroDiaDoMes;
        Assert.Empty(corpo!.Items);
        Assert.Equal(primeiro.AddDays(-30), corpo.DataInicial);
        Assert.Equal(primeiro.AddMonths(1).AddDays(-1).AddDays(30), corpo.DataFinal);
        Assert.Equal(primeiro, corpo.DataMinimaCadastro);
        Assert.Contains("\"items\":[]", await client.GetStringAsync("/api/Eventos"));
    }

    [Fact]
    public async Task Get_ComFiltro_DevolveOIntervaloInformado_MesmoHistorico()
    {
        using var client = await ClienteAsync();
        var corpo = await client.GetFromJsonAsync<EventosResponse>("/api/Eventos?dataInicial=2020-01-01&dataFinal=2020-01-31");
        Assert.Equal(new DateOnly(2020, 1, 1), corpo!.DataInicial);
        Assert.Equal(new DateOnly(2020, 1, 31), corpo.DataFinal);
        Assert.Empty(corpo.Items);
    }

    [Theory]
    [InlineData("?dataInicial=2026-09-01")]
    [InlineData("?dataFinal=2026-09-30")]
    [InlineData("?dataInicial=2026-09-30&dataFinal=2026-09-01")]
    [InlineData("?dataInicial=amanha&dataFinal=2026-09-30")]
    [InlineData("?dataInicial=2026-13-01&dataFinal=2026-14-30")]
    [InlineData("?dataInicial=01/09/2026&dataFinal=30/09/2026")]
    public async Task Get_FiltroInvalido_Retorna400(string consulta)
    {
        using var client = await ClienteAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/Eventos" + consulta)).StatusCode);
    }

    [Fact]
    public async Task Get_PorIdInexistente_Retorna404()
    {
        using var client = await ClienteAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/Eventos/{Guid.NewGuid()}")).StatusCode);
    }

    // ------------------------------------------------------------------ Swagger

    [Fact]
    public async Task Swagger_DocumentaMembrosComoGuidOuArray_ExemplosEIdempotencyKey()
    {
        using var client = factory.CreateClient();
        var doc = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"))!;

        var membros = doc["components"]!["schemas"]!["RegistrarEventoRequest"]!["properties"]!["membros"]!;
        var oneOf = membros["oneOf"]!.AsArray();
        Assert.Equal(2, oneOf.Count);
        Assert.Equal("string", (string)oneOf[0]!["type"]!);
        Assert.Equal("uuid", (string)oneOf[0]!["format"]!);
        Assert.Equal("array", (string)oneOf[1]!["type"]!);
        Assert.Contains("GUID | GUID[]", (string)membros["description"]!);
        Assert.NotNull(doc["components"]!["schemas"]!["UpdateEventoRequest"]!["properties"]!["membros"]!["oneOf"]);

        var post = doc["paths"]!["/api/Eventos"]!["post"]!;
        var idempotency = post["parameters"]!.AsArray().Single(p => (string)p!["name"]! == "Idempotency-Key")!;
        Assert.True((bool)idempotency["required"]!);
        var exemplos = post["requestBody"]!["content"]!["application/json"]!["examples"]!.AsObject();
        Assert.Equal(JsonValueKind.String, exemplos["umMembro"]!["value"]!["membros"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Array, exemplos["variosMembros"]!["value"]!["membros"]!.GetValueKind());
        foreach (var codigo in new[] { "200", "201", "400", "401", "403", "404", "409" })
            Assert.NotNull(post["responses"]![codigo]);
        Assert.NotNull(doc["paths"]!["/api/Eventos/{id}"]!["delete"]!["responses"]!["204"]);
        Assert.Contains("primeiro dia do mês atual", (string)post["description"]!);
        Assert.Contains("UTC", (string)post["description"]!);
    }
}
