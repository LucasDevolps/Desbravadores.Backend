using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

// Cobre autorização, idempotência, filtros de período, resumo fixo e exclusão lógica do
// lançamento geral (POST/GET/DELETE api/Lancamentos/Geral) sobre o provider EF Core InMemory.
// A auditoria via trigger (SESSION_CONTEXT, tabela lancamentos_deletados) só existe sobre SQL
// Server real e é coberta separadamente por LancamentosGeraisAuditoriaSqlServerTests — aqui,
// LancamentosGeraisService.DeleteAsync usa o branch não-relacional (mesma convenção de
// DbSeeder.SeedAsync), validando status HTTP e efeito na listagem, mas sem auditoria.
public class LancamentosGeraisTests
{
    // Vencimento = hoje: garante que o lançamento cai dentro da janela padrão de listagem
    // (últimos 30/60/90 dias, ver LancamentosGeraisService.ResolvePeriodo), independente de
    // quando os testes rodam.
    private static object CorpoValido() => new
    {
        tipo = "Mensalidade",
        categoria = "Clube",
        tipoFluxo = "Entrada",
        valor = 25.00m,
        vencimento = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    };

    private static async Task<HttpResponseMessage> PostGeralAsync(HttpClient client, string? idempotencyKey, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Geral")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteGeralAsync(HttpClient client, Guid id, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/Geral/{id}")
        {
            Content = body is null ? null : JsonContent.Create(body),
        };

        return await client.SendAsync(request);
    }

    // ---- Autorização ----

    [Fact]
    public async Task Create_SemToken_Retorna401ComMensagemExata()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await PostGeralAsync(client, Guid.NewGuid().ToString(), CorpoValido());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
    }

    [Theory]
    [InlineData("ADM")]
    [InlineData("DIR")]
    [InlineData("DIRA")]
    [InlineData("SEC")]
    [InlineData("TES")]
    public async Task Create_ComRolePermitida_Retorna200(string role)
    {
        using var factory = new AlmiranteApiFactory();
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);
        using var httpClient = client;

        var response = await PostGeralAsync(httpClient, Guid.NewGuid().ToString(), CorpoValido());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentoGeralResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.OperacaoId);
        Assert.True(body.UsuariosProcessados >= 1);
        Assert.Equal(body.UsuariosProcessados, body.LancamentosCriados);
    }

    [Theory]
    [InlineData("DS")]
    [InlineData("CAP")]
    public async Task Create_ComRoleNaoPermitida_Retorna401ComMensagemExata(string role)
    {
        using var factory = new AlmiranteApiFactory();
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);
        using var httpClient = client;

        var response = await PostGeralAsync(httpClient, Guid.NewGuid().ToString(), CorpoValido());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
    }

    [Fact]
    public async Task List_SemToken_Retorna401ComMensagemExata()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Lancamentos/Geral");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
    }

    [Fact]
    public async Task Delete_SemToken_Retorna401ComMensagemExata()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await DeleteGeralAsync(client, Guid.NewGuid(), new { motivo = "Qualquer" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
    }

    // Outros endpoints não deste escopo continuam com o comportamento padrão (401 sem body
    // específico, sem "ACESSO NEGADO!") — assim como já coberto por LancamentosTests e
    // CargosTests, que continuam passando sem alteração.

    // ---- Validação de entrada ----

    [Fact]
    public async Task Create_SemIdempotencyKey_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await PostGeralAsync(client, idempotencyKey: null, CorpoValido());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("TipoInvalido", "Clube")]
    [InlineData("Mensalidade", "CategoriaInvalida")]
    public async Task Create_ComTipoOuCategoriaInvalidos_Retorna400(string tipo, string categoria)
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await PostGeralAsync(client, Guid.NewGuid().ToString(), new
        {
            tipo,
            categoria,
            valor = 10m,
            vencimento = "2026-12-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ComValorNegativo_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await PostGeralAsync(client, Guid.NewGuid().ToString(), new
        {
            tipo = "Mensalidade",
            categoria = "Clube",
            tipoFluxo = "Entrada",
            valor = -1m,
            vencimento = "2026-12-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Elegibilidade e consistência ----

    [Fact]
    public async Task Create_CriaUmLancamentoPorUsuarioCadastrado()
    {
        using var factory = new AlmiranteApiFactory();
        await TestHelpers.AddUsuarioAsync(factory, "Membro 1", "DS");
        await TestHelpers.AddUsuarioAsync(factory, "Membro 2", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await PostGeralAsync(client, Guid.NewGuid().ToString(), CorpoValido());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentoGeralResponse>();
        // admin (seed) + 2 membros adicionados.
        Assert.Equal(3, body!.UsuariosProcessados);
        Assert.Equal(3, body.LancamentosCriados);

        var listResponse = await client.GetAsync("/api/Lancamentos/Geral");
        var listBody = await listResponse.Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        Assert.Equal(3, listBody!.Lancamentos.Count);
    }

    // ---- Idempotência ----

    [Fact]
    public async Task Create_ReenvioComMesmaChaveEMesmosDados_NaoDuplicaLancamentos()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var key = Guid.NewGuid().ToString();

        var primeira = await PostGeralAsync(client, key, CorpoValido());
        var segunda = await PostGeralAsync(client, key, CorpoValido());

        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);

        var corpo1 = await primeira.Content.ReadFromJsonAsync<LancamentoGeralResponse>();
        var corpo2 = await segunda.Content.ReadFromJsonAsync<LancamentoGeralResponse>();
        Assert.Equal(corpo1!.OperacaoId, corpo2!.OperacaoId);

        var listResponse = await client.GetAsync("/api/Lancamentos/Geral");
        var listBody = await listResponse.Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        Assert.Equal(corpo1.LancamentosCriados, listBody!.Lancamentos.Count);
    }

    [Fact]
    public async Task Create_ReenvioComMesmaChaveEDadosDiferentes_Retorna409()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var key = Guid.NewGuid().ToString();

        var primeira = await PostGeralAsync(client, key, CorpoValido());
        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);

        var segunda = await PostGeralAsync(client, key, new
        {
            tipo = "Mensalidade",
            categoria = "Clube",
            tipoFluxo = "Entrada",
            valor = 999.00m,
            vencimento = "2026-12-01",
        });

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
    }

    // A prova definitiva de que requisições verdadeiramente concorrentes com a mesma chave não
    // duplicam lançamentos depende do índice único em LancamentosOperacoes.IdempotencyKey
    // bloqueando/rejeitando a segunda gravação no banco — uma garantia do SQL Server real, que o
    // provider EF Core InMemory não reproduz de forma confiável entre instâncias de DbContext
    // (o insert duplicado simplesmente é aceito, sem lançar DbUpdateException). Por isso esse
    // cenário é coberto contra SQL Server real em
    // LancamentosGeraisAuditoriaSqlServerTests.RequisicoesConcorrentesComMesmaChave_CriaApenasUmaOperacao,
    // não aqui.

    // ---- Listagem: resumo fixo ----

    [Fact]
    public async Task List_SemLancamentos_RetornaArrayVazioEResumoFixo()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Lancamentos/Geral");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        Assert.Empty(body!.Lancamentos);
        Assert.Equal(800.00m, body.Resumo.TotalDespesas);
        Assert.Equal(1000.00m, body.Resumo.TotalEntradas);
        Assert.Equal(200.00m, body.Resumo.SaldoAtual);
    }

    [Fact]
    public async Task List_ComLancamentos_ResumoContinuaFixo()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        await PostGeralAsync(client, Guid.NewGuid().ToString(), CorpoValido());

        var response = await client.GetAsync("/api/Lancamentos/Geral");

        var body = await response.Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        Assert.NotEmpty(body!.Lancamentos);
        Assert.Equal(800.00m, body.Resumo.TotalDespesas);
        Assert.Equal(1000.00m, body.Resumo.TotalEntradas);
        Assert.Equal(200.00m, body.Resumo.SaldoAtual);
    }

    // ---- Listagem: filtros de período ----

    [Fact]
    public async Task List_ComPeriodoDesconhecido_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Lancamentos/Geral?periodo=15");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("30")]
    [InlineData("60")]
    [InlineData("90")]
    public async Task List_ComPeriodosPredefinidos_Retorna200(string periodo)
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync($"/api/Lancamentos/Geral?periodo={periodo}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task List_ComPeriodoPersonalizadoValido_Retorna200EFiltraPorVencimento()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var criar = await PostGeralAsync(client, Guid.NewGuid().ToString(), new
        {
            tipo = "Mensalidade",
            categoria = "Clube",
            tipoFluxo = "Entrada",
            valor = 10m,
            vencimento = "2026-12-01",
        });
        criar.EnsureSuccessStatusCode();

        var dentro = await client.GetAsync("/api/Lancamentos/Geral?periodo=personalizado&dataInicio=2026-11-01&dataFim=2026-12-31");
        var fora = await client.GetAsync("/api/Lancamentos/Geral?periodo=personalizado&dataInicio=2027-01-01&dataFim=2027-01-31");

        var dentroBody = await dentro.Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var foraBody = await fora.Content.ReadFromJsonAsync<LancamentosGeralListResponse>();

        Assert.NotEmpty(dentroBody!.Lancamentos);
        Assert.Empty(foraBody!.Lancamentos);
    }

    [Fact]
    public async Task List_ComPeriodoPersonalizadoSemDatas_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Lancamentos/Geral?periodo=personalizado");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_ComDataInicioPosteriorADataFim_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync(
            "/api/Lancamentos/Geral?periodo=personalizado&dataInicio=2026-12-31&dataFim=2026-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_ComDataInvalida_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync(
            "/api/Lancamentos/Geral?periodo=personalizado&dataInicio=31-12-2026&dataFim=2026-12-31");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Exclusão lógica ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Delete_ComMotivoVazioOuEmBranco_Retorna400(string motivo)
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await DeleteGeralAsync(client, Guid.NewGuid(), new { motivo });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ComMotivoMaiorQue255Caracteres_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await DeleteGeralAsync(client, Guid.NewGuid(), new { motivo = new string('x', 256) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Inexistente_Retorna404()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await DeleteGeralAsync(client, Guid.NewGuid(), new { motivo = "Lançamento cadastrado incorretamente." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ComSucesso_Retorna204ERemoveDaListagemAtiva()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var criar = await PostGeralAsync(client, Guid.NewGuid().ToString(), CorpoValido());
        var listInicial = await (await client.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var id = listInicial!.Lancamentos[0].Id;

        var delete = await DeleteGeralAsync(client, id, new { motivo = "Lançamento cadastrado incorretamente." });

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var listApos = await (await client.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        Assert.DoesNotContain(listApos!.Lancamentos, l => l.Id == id);
    }

    [Fact]
    public async Task Delete_Repetido_Retorna404NaSegundaTentativa()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        await PostGeralAsync(client, Guid.NewGuid().ToString(), CorpoValido());
        var lista = await (await client.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var id = lista!.Lancamentos[0].Id;

        var primeira = await DeleteGeralAsync(client, id, new { motivo = "Motivo válido." });
        var segunda = await DeleteGeralAsync(client, id, new { motivo = "Outro motivo válido." });

        Assert.Equal(HttpStatusCode.NoContent, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, segunda.StatusCode);
    }

    [Fact]
    public async Task Delete_NaoAfetaOutrosLancamentosDoMesmoLote()
    {
        using var factory = new AlmiranteApiFactory();
        await TestHelpers.AddUsuarioAsync(factory, "Membro 1", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        await PostGeralAsync(client, Guid.NewGuid().ToString(), CorpoValido());
        var lista = await (await client.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        Assert.True(lista!.Lancamentos.Count >= 2);
        var idParaExcluir = lista.Lancamentos[0].Id;
        var totalAntes = lista.Lancamentos.Count;

        var delete = await DeleteGeralAsync(client, idParaExcluir, new { motivo = "Cadastro incorreto." });
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var listaDepois = await (await client.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        Assert.Equal(totalAntes - 1, listaDepois!.Lancamentos.Count);
    }

    private record ProblemDetailsBody(string? Title, int? Status, string? Detail);
}
