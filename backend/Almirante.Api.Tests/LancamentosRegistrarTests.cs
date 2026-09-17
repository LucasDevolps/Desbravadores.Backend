using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;

namespace Almirante.Api.Tests;

// Cobre POST /api/Lancamentos/Registrar: lançamento único (membro específico ou anônimo/despesa
// do clube) ou para todos os usuários cadastrados (aplicarATodosOsMembros = true). Reaproveita
// LancamentosService/LancamentosGeraisService, já cobertos em detalhe por LancamentosTests e
// LancamentosGeraisTests — aqui o foco é a orquestração nova: os dois modos, a combinação
// inválida entre eles, e a autorização compartilhada com o lançamento geral.
public class LancamentosRegistrarTests
{
    private static string VencimentoFuturo(int dias = 30) => DateTime.UtcNow.AddDays(dias).ToString("yyyy-MM-dd");

    private static async Task<HttpResponseMessage> RegistrarAsync(HttpClient client, string? idempotencyKey, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static object CorpoUnico(Guid? membroId, string? membroNome, bool aplicarATodos = false) => new
    {
        membroId,
        membroNome,
        tipo = "Doação",
        categoria = "Clube",
        tipoFluxo = "Entrada",
        valor = 50m,
        vencimento = VencimentoFuturo(),
        status = "Pendente",
        aplicarATodosOsMembros = aplicarATodos,
    };

    [Fact]
    public async Task Registrar_SemToken_Retorna401ComMensagemExata()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await RegistrarAsync(client, null, CorpoUnico(null, "Doação anônima"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
    }

    [Fact]
    public async Task Registrar_ComRoleNaoPermitida_Retorna401ComMensagemExata()
    {
        using var factory = new AlmiranteApiFactory();
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        using var httpClient = client;

        var response = await RegistrarAsync(httpClient, null, CorpoUnico(null, "Doação anônima"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
    }

    [Fact]
    public async Task Registrar_Anonimo_CriaLancamentoSemMembroId()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, CorpoUnico(null, "Doação de empresário local"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.NotNull(created);
        Assert.Null(created!.MembroId);
        Assert.Equal("Doação de empresário local", created.MembroNome);
        Assert.Equal(TipoFluxoLancamento.Entrada, created.TipoFluxo);
    }

    [Fact]
    public async Task Registrar_Anonimo_SemMembroNome_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, CorpoUnico(null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registrar_ParaMembroEspecifico_CriaLancamentoComMembroId()
    {
        using var factory = new AlmiranteApiFactory();
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Membro Específico", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, CorpoUnico(membro.Id, membro.Nome));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.Equal(membro.Id, created!.MembroId);
    }

    [Fact]
    public async Task Registrar_Despesa_AceitaTipoFluxoDespesa()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, new
        {
            membroId = (Guid?)null,
            membroNome = "Compra de material de escritório",
            tipo = "Outros",
            categoria = "Clube",
            tipoFluxo = "Despesa",
            valor = 150m,
            vencimento = VencimentoFuturo(),
            status = "Pendente",
            aplicarATodosOsMembros = false,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.Equal(TipoFluxoLancamento.Despesa, created!.TipoFluxo);
    }

    [Fact]
    public async Task Registrar_AplicarATodosOsMembros_CriaUmLancamentoPorUsuario()
    {
        using var factory = new AlmiranteApiFactory();
        await TestHelpers.AddUsuarioAsync(factory, "Membro 1", "DS");
        await TestHelpers.AddUsuarioAsync(factory, "Membro 2", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, Guid.NewGuid().ToString(), CorpoUnico(null, null, aplicarATodos: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentoGeralResponse>();
        // admin (seed) + 2 membros adicionados.
        Assert.Equal(3, body!.LancamentosCriados);
    }

    [Fact]
    public async Task Registrar_AplicarATodosOsMembrosComMembroId_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(
            client, Guid.NewGuid().ToString(), CorpoUnico(Guid.NewGuid(), "Alguém", aplicarATodos: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registrar_AplicarATodosOsMembrosSemIdempotencyKey_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, CorpoUnico(null, null, aplicarATodos: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registrar_ComVencimentoNoPassado_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, new
        {
            membroId = (Guid?)null,
            membroNome = "Doação",
            tipo = "Doação",
            categoria = "Clube",
            tipoFluxo = "Entrada",
            valor = 50m,
            vencimento = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"),
            status = "Pendente",
            aplicarATodosOsMembros = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registrar_ComValorZero_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, new
        {
            membroId = (Guid?)null,
            membroNome = "Doação",
            tipo = "Doação",
            categoria = "Clube",
            tipoFluxo = "Entrada",
            valor = 0m,
            vencimento = VencimentoFuturo(),
            status = "Pendente",
            aplicarATodosOsMembros = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registrar_ComTipoFluxoInvalido_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await RegistrarAsync(client, null, new
        {
            membroId = (Guid?)null,
            membroNome = "Doação",
            tipo = "Doação",
            categoria = "Clube",
            tipoFluxo = "Saida",
            valor = 50m,
            vencimento = VencimentoFuturo(),
            status = "Pendente",
            aplicarATodosOsMembros = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private record ProblemDetailsBody(string? Title, int? Status, string? Detail);
}
