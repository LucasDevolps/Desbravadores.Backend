using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

public sealed class LancamentosEnumTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static Dictionary<string, object?> Body(Guid? membroId) => new()
    {
        ["membroId"] = membroId,
        ["finalidade"] = "Mensalidade",
        ["categoria"] = "Clube",
        ["tipoFluxo"] = "Entrada",
        ["valor"] = 50m,
        ["vencimento"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10),
    };

    [Theory]
    [InlineData("categoria", -1)]
    [InlineData("categoria", 2)]
    [InlineData("tipoFluxo", -1)]
    [InlineData("tipoFluxo", 2)]
    public async Task Registrar_RejeitaEnumForaDoDominio(string campo, int valor)
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Enum inválido", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var body = Body(membro.Id);
        body[campo] = valor;

        var response = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("categoria", 2)]
    [InlineData("status", 3)]
    [InlineData("tipoFluxo", 2)]
    public async Task Update_RejeitaEnumForaDoDominio(string campo, int valor)
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Atualização inválida", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var created = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(membro.Id));
        var dto = await created.Content.ReadFromJsonAsync<LancamentoDto>();

        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{dto!.Id}", new Dictionary<string, int> { [campo] = valor });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0, "Evento", 0, "Entrada", 0, "Pendente")]
    [InlineData(1, "Clube", 1, "Saida", 1, "Pago")]
    [InlineData(0, "Evento", 1, "Saida", 2, "Atrasado")]
    public async Task RegistrarEAtualizar_AceitamCodigosDosEnums_EPreservamNomesNaResposta(
        int categoria, string nomeCategoria, int fluxo, string nomeFluxo, int status, string nomeStatus)
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, $"Códigos {Guid.NewGuid():N}", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var body = Body(membro.Id);
        body["categoria"] = categoria;
        body["tipoFluxo"] = fluxo;
        var created = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(nomeCategoria, json.RootElement.GetProperty("categoria").GetString());
        Assert.Equal(nomeFluxo, json.RootElement.GetProperty("tipoFluxo").GetString());
        Assert.Equal("Pendente", json.RootElement.GetProperty("status").GetString());
        var id = json.RootElement.GetProperty("id").GetGuid();

        var updated = await client.PutAsJsonAsync($"/api/Lancamentos/{id}", new { status });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(nomeStatus, updatedJson.RootElement.GetProperty("status").GetString());

        var list = await client.GetFromJsonAsync<LancamentosResponse>($"/api/Lancamentos?search={Uri.EscapeDataString(membro.Nome)}&status={status}");
        Assert.Equal(id, Assert.Single(list!.Items).Id);
    }

    [Fact]
    public async Task RegistrarTodos_RejeitaChaveMaiorQueAColunaPersistida()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var body = Body(null);
        body["aplicarATodosOsMembros"] = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", new string('a', 101));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registrar_LocationPermiteConsultarLancamento_EIgnoraExcluido()
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Location", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var created = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", Body(membro.Id));
        var dto = await created.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.EndsWith($"/api/Lancamentos/{dto!.Id}", created.Headers.Location!.AbsolutePath);
        var consultado = await client.GetFromJsonAsync<LancamentoDto>(created.Headers.Location);
        Assert.Equal(dto.Id, consultado!.Id);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, created.Headers.Location)
        {
            Content = JsonContent.Create(new { motivo = "Correção" }),
        };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(created.Headers.Location)).StatusCode);
    }
}
