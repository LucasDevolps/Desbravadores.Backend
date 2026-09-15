using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

public class CargosTests
{
    [Fact]
    public async Task List_SemToken_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Cargos");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_ComToken_RetornaCargosDoSeedDemonstrativo()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Cargos");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cargos = await response.Content.ReadFromJsonAsync<List<CargoDto>>();
        Assert.NotNull(cargos);
        Assert.NotEmpty(cargos!);
        Assert.Contains(cargos, c => c.Role == "DS" && c.Nome == "Desbravador");
        Assert.Contains(cargos, c => c.Role == "DIR" && c.Nome == "Diretor");
        Assert.All(cargos, c => Assert.True(c.Ativo));
    }
}
