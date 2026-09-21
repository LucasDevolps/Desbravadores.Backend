using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Matriz RBAC (Security/Roles.cs): diretoria (ADM, DIR, DIRA, SEC, TES) administra lançamentos e
// lista usuários/cargos; demais cargos só consultam o próprio perfil.
public sealed class AuthorizationTests : IClassFixture<AlmiranteApiFactory>
{
    private readonly AlmiranteApiFactory factory;
    public AuthorizationTests(AlmiranteApiFactory factory) => this.factory = factory;

    private static object NovoLancamento(Guid? membroId, bool todos = false) => new
    {
        membroId, finalidade = "Mensalidade", descricao = "RBAC", categoria = "Clube", tipoFluxo = "Entrada", valor = 10m,
        vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = todos,
    };

    private static HttpRequestMessage Endpoint(string nome) => nome switch
    {
        "GET /api/Usuarios" => new(HttpMethod.Get, "/api/Usuarios"),
        "GET /api/Cargos" => new(HttpMethod.Get, "/api/Cargos"),
        "GET /api/Lancamentos" => new(HttpMethod.Get, "/api/Lancamentos"),
        "GET /api/Lancamentos/{id}" => new(HttpMethod.Get, $"/api/Lancamentos/{Guid.NewGuid()}"),
        "POST /api/Lancamentos/Registrar" => new(HttpMethod.Post, "/api/Lancamentos/Registrar") { Content = JsonContent.Create(NovoLancamento(Guid.NewGuid())) },
        "PUT /api/Lancamentos/{id}" => new(HttpMethod.Put, $"/api/Lancamentos/{Guid.NewGuid()}") { Content = JsonContent.Create(new { status = "Pago" }) },
        "DELETE /api/Lancamentos/{id}" => new(HttpMethod.Delete, $"/api/Lancamentos/{Guid.NewGuid()}") { Content = JsonContent.Create(new { motivo = "rbac" }) },
        _ => throw new ArgumentOutOfRangeException(nameof(nome)),
    };

    public static TheoryData<string> EndpointsAdministrativos => new()
    {
        "GET /api/Usuarios", "GET /api/Cargos", "GET /api/Lancamentos", "GET /api/Lancamentos/{id}", "POST /api/Lancamentos/Registrar",
        "PUT /api/Lancamentos/{id}", "DELETE /api/Lancamentos/{id}",
    };

    [Theory]
    [MemberData(nameof(EndpointsAdministrativos))]
    public async Task SemAutenticacao_Retorna401(string endpoint)
    {
        using var client = factory.CreateClient();
        var response = await client.SendAsync(Endpoint(endpoint));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Theory]
    [MemberData(nameof(EndpointsAdministrativos))]
    public async Task TokenInvalido_Retorna401(string endpoint)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "abc.def.ghi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Endpoint(endpoint))).StatusCode);
    }

    public static TheoryData<string, string> CargosSemPermissao()
    {
        var data = new TheoryData<string, string>();
        foreach (var role in new[] { "DS", "CONS", "TESU", "CAP" })
        foreach (var endpoint in EndpointsAdministrativos) data.Add(role, endpoint);
        return data;
    }

    [Theory]
    [MemberData(nameof(CargosSemPermissao))]
    public async Task AutenticadoSemPermissao_Retorna403(string role, string endpoint)
    {
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);
        var response = await client.SendAsync(Endpoint(endpoint));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
        // O próprio perfil continua disponível para qualquer autenticado.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);
    }

    [Theory]
    [InlineData("ADM")] [InlineData("DIR")] [InlineData("DIRA")] [InlineData("SEC")] [InlineData("TES")]
    public async Task Diretoria_AcessaEndpointsAdministrativos_EOperacoesSaoAuditadas(string role)
    {
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);
        var membro = await TestHelpers.AddUsuarioAsync(factory, $"Membro {role}", "DS");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Cargos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Lancamentos")).StatusCode);

        var criado = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", NovoLancamento(membro.Id));
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var dto = await criado.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/Lancamentos/{dto!.Id}", new { status = "Pago" })).StatusCode);
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{dto.Id}") { Content = JsonContent.Create(new { motivo = "rbac" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);

        // Responsável pela operação geral = usuário autenticado (sub -> NameIdentifier), não o cliente.
        var geral = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar") { Content = JsonContent.Create(NovoLancamento(null, todos: true)) };
        geral.Headers.Add("Idempotency-Key", $"rbac-{Guid.NewGuid():N}");
        var resposta = await client.SendAsync(geral);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var operacaoId = (await resposta.Content.ReadFromJsonAsync<LancamentoGeralResponse>())!.OperacaoId;
        var criadoPor = await TestHelpers.WithDbAsync(factory, db =>
            db.LancamentosOperacoes.Where(o => o.Id == operacaoId).Select(o => o.CriadoPorUsuarioId).SingleAsync());
        Assert.Equal(usuarioId, criadoPor);
    }

    [Fact]
    public async Task MudancaDeCargoNoBanco_InvalidaTokenAnterior_ENovoLoginRecebe403()
    {
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Lancamentos")).StatusCode);

        var email = await TestHelpers.WithDbAsync(factory, async db =>
        {
            var usuario = await db.Usuarios.SingleAsync(u => u.Id == usuarioId);
            usuario.CargoId = await db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync();
            await db.SaveChangesAsync();
            return usuario.Email;
        });

        // A role do token antigo não confere mais com o cargo atual: sessão rejeitada.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Lancamentos")).StatusCode);

        using var novo = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novo);
        var login = await TestHelpers.PostLoginAsync(novo, email, TestHelpers.SenhaPadraoTeste);
        novo.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await novo.GetAsync("/api/Lancamentos")).StatusCode);
    }
}
