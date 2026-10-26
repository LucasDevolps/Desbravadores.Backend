using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

/// <summary>
/// Matriz de permissões da issue #127, endpoint por endpoint e perfil por perfil. "Permitido" = a autorização deixou passar (nunca 401/403;
/// o corpo propositalmente inválido ou o id inexistente produzem 400/404); "negado" = 403 com o ProblemDetails padrão.
/// </summary>
public sealed class AuthorizationTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    public enum Modulo { Cadastros, Financeiro, Eventos, Membros, Painel, Formacao, Autoatendimento, SenhasTerceiros }

    private static readonly Modulo[] Todos = Enum.GetValues<Modulo>();

    /// <summary>Fonte única do esperado: docs/authentication-security.md descreve a mesma tabela.</summary>
    private static readonly Dictionary<string, Modulo[]> Matriz = new()
    {
        ["ADM"] = Todos,
        ["DIR"] = Todos,
        // Issue #159: DIRA tem todos os módulos, menos a senha de terceiros e a recuperação assistida.
        ["DIRA"] = [.. Todos.Except([Modulo.SenhasTerceiros])],
        ["SEC"] = [Modulo.Cadastros, Modulo.Eventos, Modulo.Membros, Modulo.Painel, Modulo.Formacao, Modulo.Autoatendimento, Modulo.SenhasTerceiros],
        ["TES"] = [Modulo.Financeiro, Modulo.Eventos, Modulo.Membros, Modulo.Painel, Modulo.Autoatendimento],
        ["DS"] = [Modulo.Autoatendimento],
        ["CONS"] = [Modulo.Autoatendimento],
        ["TESU"] = [Modulo.Autoatendimento],
        ["SECU"] = [Modulo.Autoatendimento],
        ["CAP"] = [Modulo.Autoatendimento],
        ["ALM"] = [Modulo.Autoatendimento],
    };

    private static object NovoLancamento(Guid? membroId, bool todos = false) => new
    {
        membroId, finalidade = "Mensalidade", descricao = "RBAC", categoria = "Clube", tipoFluxo = "Entrada", valor = 10m,
        vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = todos,
    };

    private static HttpRequestMessage Json(HttpMethod metodo, string rota, object corpo) => new(metodo, rota) { Content = JsonContent.Create(corpo) };

    public sealed record Endpoint(string Nome, Modulo Modulo, Func<HttpRequestMessage> Requisicao);

    public static readonly Endpoint[] Endpoints =
    [
        new("GET /api/Usuarios", Modulo.Cadastros, () => new(HttpMethod.Get, "/api/Usuarios")),
        new("POST /api/Usuarios", Modulo.Cadastros, () => Json(HttpMethod.Post, "/api/Usuarios", new { nome = "RBAC", email = $"rbac-{Guid.NewGuid():N}@local.dev", senha = "Forte-Senha-9x2k", cargoId = Guid.NewGuid(), cpf = TestHelpers.NovoCpf(), rg = TestHelpers.RgFicticio })),
        new("PUT /api/Usuarios/{id}", Modulo.Cadastros, () => Json(HttpMethod.Put, $"/api/Usuarios/{Guid.NewGuid()}", new { })),
        new("DELETE /api/Usuarios/{id}", Modulo.Cadastros, () => new(HttpMethod.Delete, $"/api/Usuarios/{Guid.NewGuid()}")),
        new("GET /api/Usuarios/{id}/foto", Modulo.Cadastros, () => new(HttpMethod.Get, $"/api/Usuarios/{Guid.NewGuid()}/foto")),
        new("PUT /api/Usuarios/{id}/foto", Modulo.Cadastros, () => new(HttpMethod.Put, $"/api/Usuarios/{Guid.NewGuid()}/foto")),
        new("DELETE /api/Usuarios/{id}/foto", Modulo.Cadastros, () => new(HttpMethod.Delete, $"/api/Usuarios/{Guid.NewGuid()}/foto")),
        new("GET /api/Cargos", Modulo.Cadastros, () => new(HttpMethod.Get, "/api/Cargos")),

        new("GET /api/Lancamentos", Modulo.Financeiro, () => new(HttpMethod.Get, "/api/Lancamentos")),
        new("GET /api/Lancamentos/{id}", Modulo.Financeiro, () => new(HttpMethod.Get, $"/api/Lancamentos/{Guid.NewGuid()}")),
        new("POST /api/Lancamentos/Registrar", Modulo.Financeiro, () => Json(HttpMethod.Post, "/api/Lancamentos/Registrar", NovoLancamento(Guid.NewGuid()))),
        new("PUT /api/Lancamentos/{id}", Modulo.Financeiro, () => Json(HttpMethod.Put, $"/api/Lancamentos/{Guid.NewGuid()}", new { status = "Pago" })),
        new("POST /api/Lancamentos/{id}/Pagamento", Modulo.Financeiro, () => Json(HttpMethod.Post, $"/api/Lancamentos/{Guid.NewGuid()}/Pagamento", new { dataPagamento = "2026-10-01", valorPago = 10m })),
        new("PUT /api/Lancamentos/{id}/Pagamento", Modulo.Financeiro, () => Json(HttpMethod.Put, $"/api/Lancamentos/{Guid.NewGuid()}/Pagamento", new { dataPagamento = "2026-10-01", valorPago = 10m })),
        new("DELETE /api/Lancamentos/{id}", Modulo.Financeiro, () => Json(HttpMethod.Delete, $"/api/Lancamentos/{Guid.NewGuid()}", new { motivo = "rbac" })),

        new("GET /api/Eventos", Modulo.Eventos, () => new(HttpMethod.Get, "/api/Eventos")),
        new("GET /api/Eventos/{id}", Modulo.Eventos, () => new(HttpMethod.Get, $"/api/Eventos/{Guid.NewGuid()}")),
        new("POST /api/Eventos", Modulo.Eventos, () => Json(HttpMethod.Post, "/api/Eventos", EventosTestKit.Corpo(Guid.NewGuid()))),
        new("PUT /api/Eventos/{id}", Modulo.Eventos, () => Json(HttpMethod.Put, $"/api/Eventos/{Guid.NewGuid()}", EventosTestKit.Corpo(Guid.NewGuid(), versao: "AAAAAAAAB9E="))),
        new("DELETE /api/Eventos/{id}", Modulo.Eventos, () => Json(HttpMethod.Delete, $"/api/Eventos/{Guid.NewGuid()}", new { motivo = "rbac", versao = "AAAAAAAAB9E=" })),

        new("GET /api/Membros", Modulo.Membros, () => new(HttpMethod.Get, "/api/Membros")),
        new("GET /api/Dashboard", Modulo.Painel, () => new(HttpMethod.Get, "/api/Dashboard")),

        new("GET /api/Auth/Me", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Auth/Me")),
        new("GET /api/Me", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Me")),
        new("PUT /api/Me", Modulo.Autoatendimento, () => Json(HttpMethod.Put, "/api/Me", new { cpf = "123" })),
        new("GET /api/Me/foto", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Me/foto")),
        new("PUT /api/Me/foto", Modulo.Autoatendimento, () => new(HttpMethod.Put, "/api/Me/foto")),
        new("DELETE /api/Me/foto", Modulo.Autoatendimento, () => new(HttpMethod.Delete, "/api/Me/foto")),
        new("GET /api/Me/Eventos", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Me/Eventos")),
        new("GET /api/Me/Eventos/{id}", Modulo.Autoatendimento, () => new(HttpMethod.Get, $"/api/Me/Eventos/{Guid.NewGuid()}")),
        new("GET /api/Cep/{cep}", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Cep/04382160")),
        new("GET /api/Notificacoes/Pendentes", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Notificacoes/Pendentes")),
        new("POST /api/Notificacoes/{id}/Leitura", Modulo.Autoatendimento, () => new(HttpMethod.Post, $"/api/Notificacoes/{Guid.NewGuid()}/Leitura")),

        // Issue #159: a própria senha para qualquer cargo; senha de terceiros e recuperação assistida só ADM, DIR e SEC (o par
        // solicitante/alvo ainda passa pela matriz de PermissoesSenha, coberta em AlteracaoSenhaSqlServerTests).
        new("POST /api/Auth/alterar-senha", Modulo.Autoatendimento, () => Json(HttpMethod.Post, "/api/Auth/alterar-senha", new { })),
        new("POST /api/Usuarios/{usuarioId}/alterar-senha", Modulo.SenhasTerceiros,
            () => Json(HttpMethod.Post, $"/api/Usuarios/{Guid.NewGuid()}/alterar-senha", new { })),
        new("GET /api/Usuarios/recuperacao-senha/pendentes", Modulo.SenhasTerceiros, () => new(HttpMethod.Get, "/api/Usuarios/recuperacao-senha/pendentes")),
        new("POST /api/Usuarios/{usuarioId}/recuperacao-senha/autorizacao", Modulo.SenhasTerceiros,
            () => Json(HttpMethod.Post, $"/api/Usuarios/{Guid.NewGuid()}/recuperacao-senha/autorizacao", new { })),

        // Issue #125: catálogos e a própria formação para qualquer cargo; escrita e dados de outros membros só para ADM, DIR, DIRA e SEC.
        new("GET /api/Classes", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Classes")),
        new("GET /api/Classes/{classe}", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Classes/Amigo")),
        new("GET /api/Classes/{classe}/Revisoes", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Classes/LiderMaster/Revisoes")),
        new("GET /api/Especialidades", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Especialidades")),
        new("GET /api/Especialidades/{id}", Modulo.Autoatendimento, () => new(HttpMethod.Get, $"/api/Especialidades/{Guid.NewGuid()}")),
        new("GET /api/Especialidades/{id}/Revisoes", Modulo.Autoatendimento, () => new(HttpMethod.Get, $"/api/Especialidades/{Guid.NewGuid()}/Revisoes")),
        new("GET /api/RevisoesRequisitos/{id}", Modulo.Autoatendimento, () => new(HttpMethod.Get, $"/api/RevisoesRequisitos/{Guid.NewGuid()}")),
        new("GET /api/Me/Formacao", Modulo.Autoatendimento, () => new(HttpMethod.Get, "/api/Me/Formacao")),
        new("GET /api/Me/Formacao/Classes/{id}", Modulo.Autoatendimento, () => new(HttpMethod.Get, $"/api/Me/Formacao/Classes/{Guid.NewGuid()}")),
        new("GET /api/Me/Formacao/Especialidades/{id}", Modulo.Autoatendimento, () => new(HttpMethod.Get, $"/api/Me/Formacao/Especialidades/{Guid.NewGuid()}")),

        new("POST /api/Especialidades", Modulo.Formacao, () => Json(HttpMethod.Post, "/api/Especialidades", new { })),
        new("PUT /api/Especialidades/{id}", Modulo.Formacao, () => Json(HttpMethod.Put, $"/api/Especialidades/{Guid.NewGuid()}", new { })),
        new("DELETE /api/Especialidades/{id}", Modulo.Formacao, () => Json(HttpMethod.Delete, $"/api/Especialidades/{Guid.NewGuid()}", new { })),
        new("POST /api/Especialidades/{id}/Reativar", Modulo.Formacao, () => Json(HttpMethod.Post, $"/api/Especialidades/{Guid.NewGuid()}/Reativar", new { })),
        new("POST /api/RevisoesRequisitos", Modulo.Formacao, () => Json(HttpMethod.Post, "/api/RevisoesRequisitos", new { })),
        new("POST /api/RevisoesRequisitos/{id}/Publicar", Modulo.Formacao, () => Json(HttpMethod.Post, $"/api/RevisoesRequisitos/{Guid.NewGuid()}/Publicar", new { })),
        new("POST /api/RevisoesRequisitos/{id}/Descartar", Modulo.Formacao, () => Json(HttpMethod.Post, $"/api/RevisoesRequisitos/{Guid.NewGuid()}/Descartar", new { })),
        new("GET /api/Formacao/Membros/{membroId}", Modulo.Formacao, () => new(HttpMethod.Get, $"/api/Formacao/Membros/{Guid.NewGuid()}")),
        new("GET /api/Formacao/Relatorios/Participantes", Modulo.Formacao, () => new(HttpMethod.Get, "/api/Formacao/Relatorios/Participantes")),
        .. EndpointsDeVinculo("Classes", "Investidura"),
        new("POST /api/Formacao/Classes/{id}/Requisitos/{requisitoId}/Evidencias", Modulo.Formacao,
            () => Json(HttpMethod.Post, $"/api/Formacao/Classes/{Guid.NewGuid()}/Requisitos/{Guid.NewGuid()}/Evidencias", new { })),
        new("DELETE /api/Formacao/Classes/{id}/Requisitos/{requisitoId}/Evidencias/{membroEspecialidadeId}", Modulo.Formacao,
            () => Json(HttpMethod.Delete, $"/api/Formacao/Classes/{Guid.NewGuid()}/Requisitos/{Guid.NewGuid()}/Evidencias/{Guid.NewGuid()}", new { })),
        new("PUT /api/Formacao/Classes/{id}/ValidacoesExternas/{codigo}", Modulo.Formacao,
            () => Json(HttpMethod.Put, $"/api/Formacao/Classes/{Guid.NewGuid()}/ValidacoesExternas/APROVACAO_OFICIAL", new { })),
        .. EndpointsDeVinculo("Especialidades", "Entrega"),
    ];

    /// <summary>Ações comuns de /api/Formacao/Classes e /api/Formacao/Especialidades; corpo vazio = 400 para quem passa na autorização.</summary>
    private static IEnumerable<Endpoint> EndpointsDeVinculo(string recurso, string reconhecimento)
    {
        var rota = $"/api/Formacao/{recurso}";
        return
        [
            new($"GET {rota}", Modulo.Formacao, () => new(HttpMethod.Get, rota)),
            new($"GET {rota}/{{id}}", Modulo.Formacao, () => new(HttpMethod.Get, $"{rota}/{Guid.NewGuid()}")),
            new($"GET {rota}/{{id}}/Historico", Modulo.Formacao, () => new(HttpMethod.Get, $"{rota}/{Guid.NewGuid()}/Historico")),
            new($"POST {rota}", Modulo.Formacao, () => Json(HttpMethod.Post, rota, new { })),
            new($"POST {rota}/Historicos", Modulo.Formacao, () => Json(HttpMethod.Post, $"{rota}/Historicos", new { })),
            new($"PUT {rota}/{{id}}", Modulo.Formacao, () => Json(HttpMethod.Put, $"{rota}/{Guid.NewGuid()}", new { })),
            new($"PUT {rota}/{{id}}/Requisitos/{{requisitoId}}", Modulo.Formacao, () => Json(HttpMethod.Put, $"{rota}/{Guid.NewGuid()}/Requisitos/{Guid.NewGuid()}", new { })),
            new($"POST {rota}/{{id}}/Concluir", Modulo.Formacao, () => Json(HttpMethod.Post, $"{rota}/{Guid.NewGuid()}/Concluir", new { })),
            new($"POST {rota}/{{id}}/Cancelar", Modulo.Formacao, () => Json(HttpMethod.Post, $"{rota}/{Guid.NewGuid()}/Cancelar", new { })),
            new($"POST {rota}/{{id}}/Retomar", Modulo.Formacao, () => Json(HttpMethod.Post, $"{rota}/{Guid.NewGuid()}/Retomar", new { })),
            new($"POST {rota}/{{id}}/Reabrir", Modulo.Formacao, () => Json(HttpMethod.Post, $"{rota}/{Guid.NewGuid()}/Reabrir", new { })),
            new($"POST {rota}/{{id}}/MigrarRevisao", Modulo.Formacao, () => Json(HttpMethod.Post, $"{rota}/{Guid.NewGuid()}/MigrarRevisao", new { })),
            new($"POST {rota}/{{id}}/{reconhecimento}", Modulo.Formacao, () => Json(HttpMethod.Post, $"{rota}/{Guid.NewGuid()}/{reconhecimento}", new { })),
            new($"DELETE {rota}/{{id}}/{reconhecimento}", Modulo.Formacao, () => Json(HttpMethod.Delete, $"{rota}/{Guid.NewGuid()}/{reconhecimento}", new { })),
        ];
    }

    public static TheoryData<string> Perfis => new(Matriz.Keys);

    private static async Task<List<string>> DivergenciasAsync(HttpClient client, Func<Endpoint, HttpStatusCode, string?> avaliar)
    {
        var divergencias = new List<string>();
        foreach (var endpoint in Endpoints)
        {
            using var response = await client.SendAsync(endpoint.Requisicao());
            if (avaliar(endpoint, response.StatusCode) is { } erro)
            {
                divergencias.Add($"{endpoint.Nome}: {erro} (obtido {(int)response.StatusCode})");
            }
        }

        return divergencias;
    }

    [Fact]
    public void Matriz_CobreTodasAsActionsDosControllers()
    {
        var rotas = typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Microsoft.AspNetCore.Mvc.ControllerBase)) && !t.IsAbstract)
            // Inclui ações herdadas de controllers base do projeto (ex.: FormacaoVinculosControllerBase), com a rota do controller concreto.
            .SelectMany(t => t.GetMethods().Where(m => m.IsPublic && m.DeclaringType!.Assembly == typeof(Program).Assembly)
                .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), false)
                    .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                    .Select(a => (Controller: t, Metodo: a.HttpMethods.Single(), a.Template))))
            .ToList();

        // Login, refresh, logout, csrf e a recuperação de senha sem login são anônimos por desenho; os demais precisam estar na matriz.
        var anonimos = new[] { "POST /api/Auth/login", "POST /api/Auth/refresh", "POST /api/Auth/logout", "GET /api/Auth/csrf",
            "POST /api/Auth/recuperacao-senha/solicitar", "POST /api/Auth/recuperacao-senha/confirmar" };
        var semMatriz = rotas
            .Select(r => $"{r.Metodo} /{((Microsoft.AspNetCore.Mvc.RouteAttribute)r.Controller.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), true).Single()).Template}"
                + (string.IsNullOrEmpty(r.Template) ? "" : "/" + r.Template))
            .Select(r => r.Replace(":guid", ""))
            .Where(r => !anonimos.Contains(r) && !Endpoints.Any(e => e.Nome == r))
            .ToList();
        Assert.Empty(semMatriz);
    }

    [Fact]
    public async Task SemAutenticacao_TokenInvalido_TodosOsEndpointsRetornam401()
    {
        using var anonimo = factory.CreateClient();
        Assert.Empty(await DivergenciasAsync(anonimo, (_, status) => status == HttpStatusCode.Unauthorized ? null : "esperado 401 sem token"));

        using var invalido = factory.CreateClient();
        invalido.DefaultRequestHeaders.Authorization = new("Bearer", "abc.def.ghi");
        Assert.Empty(await DivergenciasAsync(invalido, (_, status) => status == HttpStatusCode.Unauthorized ? null : "esperado 401 com token inválido"));

        using var semPermissao = factory.CreateClient();
        var resposta = await semPermissao.GetAsync("/api/Me");
        Assert.Contains(resposta.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task TokenExpirado_ESessaoRevogada_Retornam401_MesmoParaQuemTinhaPermissao()
    {
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DIR");
        var token = NotificacoesTestKit.Token(client);

        using var expirado = factory.CreateClient();
        expirado.DefaultRequestHeaders.Authorization = new("Bearer",
            NotificacoesTestKit.TokenReassinado(factory, token, DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow.AddSeconds(-60)));
        Assert.Empty(await DivergenciasAsync(expirado, (_, status) => status == HttpStatusCode.Unauthorized ? null : "esperado 401 com token expirado"));

        var sessao = NotificacoesTestKit.Sessao(token);
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            var s = await db.AuthSessions.SingleAsync(x => x.Id == sessao);
            s.RevokedAtUtc = DateTime.UtcNow;
            s.RevocationReason = "logout";
            return await db.SaveChangesAsync();
        });
        Assert.Empty(await DivergenciasAsync(client, (_, status) => status == HttpStatusCode.Unauthorized ? null : "esperado 401 com sessão revogada"));
    }

    [Fact]
    public async Task UsuarioInativo_ECargoInativo_PerdemTodoOAcesso()
    {
        using var isolada = new AlmiranteApiFactory();
        var (usuario, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(isolada, "SEC");
        var (almoxarife, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(isolada, "ALM");
        Assert.Equal(HttpStatusCode.OK, (await usuario.GetAsync("/api/Usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await almoxarife.GetAsync("/api/Me")).StatusCode);

        await TestHelpers.WithDbAsync(isolada, async db =>
        {
            (await db.Usuarios.SingleAsync(u => u.Id == usuarioId)).Ativo = false;
            (await db.Cargos.SingleAsync(c => c.Role == "ALM")).Ativo = false;
            return await db.SaveChangesAsync();
        });

        Assert.Empty(await DivergenciasAsync(usuario, (_, status) => status == HttpStatusCode.Unauthorized ? null : "esperado 401 com usuário inativo"));
        Assert.Empty(await DivergenciasAsync(almoxarife, (_, status) => status == HttpStatusCode.Unauthorized ? null : "esperado 401 com cargo inativo"));
    }

    [Theory]
    [MemberData(nameof(Perfis))]
    public async Task MatrizDePermissoes_PorEndpointEPerfil(string role)
    {
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);
        var permitidos = Matriz[role];

        var divergencias = await DivergenciasAsync(client, (endpoint, status) =>
            permitidos.Contains(endpoint.Modulo)
                ? status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "esperado acesso permitido" : null
                : status == HttpStatusCode.Forbidden ? null : "esperado 403");

        Assert.True(divergencias.Count == 0, $"{role}: " + string.Join(" | ", divergencias));
    }

    [Fact]
    public async Task AcessoNegado_UsaProblemDetailsPadrao_ESessaoContinuaValida()
    {
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");

        var response = await client.GetAsync("/api/Lancamentos");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal("ACESSO NEGADO!", problem!.Title);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Me")).StatusCode);
    }

    [Theory]
    [InlineData("ADM")] [InlineData("DIR")] [InlineData("DIRA")] [InlineData("TES")]
    public async Task Financeiro_OperaLancamentosEPagamentos_EOperacoesSaoAuditadas(string role)
    {
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);
        var membro = await TestHelpers.AddUsuarioAsync(factory, $"Membro {role}", "DS");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Lancamentos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Eventos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Membros")).StatusCode);

        var criado = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", NovoLancamento(membro.Id));
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var dto = await criado.Content.ReadFromJsonAsync<LancamentoDto>();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/Lancamentos/{dto!.Id}", new { status = "Atrasado" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(client, dto.Id, dto.Valor)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Dashboard")).StatusCode);
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{dto.Id}") { Content = JsonContent.Create(new { motivo = "rbac" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);

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
    public async Task Secretario_NaoRegistraCorrigeNemExcluiOperacoesFinanceiras_ENadaEhGravado()
    {
        var (tesoureiro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var (secretario, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "SEC");
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Membro cobrado", "DS");
        var dto = (await (await tesoureiro.PostAsJsonAsync("/api/Lancamentos/Registrar", NovoLancamento(membro.Id)))
            .Content.ReadFromJsonAsync<LancamentoDto>())!;
        var antes = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync());

        var tentativas = new[]
        {
            await secretario.PostAsJsonAsync("/api/Lancamentos/Registrar", NovoLancamento(membro.Id)),
            await secretario.PutAsJsonAsync($"/api/Lancamentos/{dto.Id}", new { status = "Pago", valor = 1m }),
            await TestHelpers.RegistrarPagamentoAsync(secretario, dto.Id, dto.Valor),
            await secretario.PutAsJsonAsync($"/api/Lancamentos/{dto.Id}/Pagamento", new { dataPagamento = TestHelpers.DataPagamentoPassada.ToString("yyyy-MM-dd"), valorPago = 1m }),
            await secretario.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{dto.Id}") { Content = JsonContent.Create(new { motivo = "sec" }) }),
            await secretario.GetAsync($"/api/Lancamentos/{dto.Id}"),
        };

        Assert.All(tentativas, t => Assert.Equal(HttpStatusCode.Forbidden, t.StatusCode));
        var salvo = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == dto.Id));
        Assert.Equal((StatusLancamento.Pendente, (decimal?)null, true, 10m), (salvo.Status, salvo.ValorPago, salvo.Ativo, salvo.Valor));
        Assert.Equal(antes, await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync()));
    }

    [Fact]
    public async Task Tesoureiro_NaoGerenciaUsuariosCargosNemFotosDeTerceiros_MasConsultaMembrosEAPropriaConta()
    {
        var (tesoureiro, tesoureiroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var alvo = await TestHelpers.AddUsuarioAsync(factory, "Alvo do tesoureiro", "DS");
        var email = $"tes-{Guid.NewGuid():N}@local.dev";
        var cargoDiretor = await TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == "DIR").Select(c => c.Id).SingleAsync());

        var tentativas = new[]
        {
            await tesoureiro.GetAsync("/api/Usuarios"),
            await tesoureiro.PostAsJsonAsync("/api/Usuarios", new { nome = "Novo", email, senha = "Forte-Senha-9x2k", cargoId = cargoDiretor, cpf = TestHelpers.NovoCpf(), rg = TestHelpers.RgFicticio }),
            await tesoureiro.PutAsJsonAsync($"/api/Usuarios/{tesoureiroId}", new { nome = "Eu", email = alvo.Email, cargoId = cargoDiretor }),
            await tesoureiro.PutAsJsonAsync($"/api/Usuarios/{alvo.Id}", new { nome = "Outro", email = alvo.Email, cargoId = cargoDiretor }),
            await tesoureiro.DeleteAsync($"/api/Usuarios/{alvo.Id}"),
            await tesoureiro.GetAsync("/api/Cargos"),
            await tesoureiro.GetAsync($"/api/Usuarios/{alvo.Id}/foto"),
            await tesoureiro.PutAsync($"/api/Usuarios/{alvo.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(100, 100))),
            await tesoureiro.DeleteAsync($"/api/Usuarios/{alvo.Id}/foto"),
        };

        Assert.All(tentativas, t => Assert.Equal(HttpStatusCode.Forbidden, t.StatusCode));
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AnyAsync(u => u.Email == email)));
        var (cargoDoTesoureiro, alvoAtivo) = await TestHelpers.WithDbAsync(factory, async db =>
            ((await db.Usuarios.Include(u => u.Cargo).SingleAsync(u => u.Id == tesoureiroId)).Cargo!.Role,
                (await db.Usuarios.SingleAsync(u => u.Id == alvo.Id)).Ativo));
        Assert.Equal(("TES", true), (cargoDoTesoureiro, alvoAtivo));

        Assert.Equal(HttpStatusCode.OK, (await tesoureiro.GetAsync("/api/Membros")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tesoureiro.GetAsync("/api/Me")).StatusCode);
    }

    [Fact]
    public async Task MudancaDeCargoNoBanco_InvalidaTokenAnterior_ENovoLoginRecebeSoAsNovasPermissoes()
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

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Lancamentos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Me")).StatusCode);

        using var novo = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novo);
        var login = await TestHelpers.PostLoginAsync(novo, email, TestHelpers.SenhaPadraoTeste);
        novo.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await novo.GetAsync("/api/Lancamentos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await novo.GetAsync("/api/Me")).StatusCode);
    }
}
