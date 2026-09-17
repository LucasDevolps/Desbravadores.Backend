using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Almirante.Api.Tests;

// Contrato de CSRF da SPA (docs/authentication-security.md): o token CSRF protege as operações
// que usam o cookie de refresh e NÃO depende de o bearer estar presente, válido ou expirado.
public class AuthCsrfTests
{
    [Fact]
    public async Task CsrfObtidoComBearer_PermiteRefreshSemAuthorization()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var token = await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var csrf = await AuthFlow.GetCsrfAsync(client, jar, bearer: token);

        var refresh = await AuthFlow.RefreshAsync(client, jar, csrf);

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    [Fact]
    public async Task BearerExpirado_NaoImpedeRefreshNemLogoutComOCsrfDaSessao()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var token = await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var csrf = await AuthFlow.GetCsrfAsync(client, jar, bearer: token);
        var expirado = TestTokens.Expired(token);

        var refresh = await AuthFlow.RefreshAsync(client, jar, csrf, bearer: expirado);
        var logout = await AuthFlow.LogoutAsync(client, jar.Clone(), csrf, bearer: expirado);
        var depois = await AuthFlow.RefreshAsync(client, jar, csrf);

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, depois.StatusCode);
    }

    [Fact]
    public async Task RefreshComBearerValido_ECsrfAnonimo_Funciona()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var csrfAnonimo = await AuthFlow.GetCsrfAsync(client, jar);
        var token = await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);

        var refresh = await AuthFlow.RefreshAsync(client, jar, csrfAnonimo, bearer: token);

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    [Fact]
    public async Task LogoutRepetido_ComOMesmoBearerJaRevogado_EhIdempotente()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var token = await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var csrf = await AuthFlow.GetCsrfAsync(client, jar, bearer: token);
        var antesDoLogout = jar.Clone();

        var primeiro = await AuthFlow.LogoutAsync(client, jar, csrf, bearer: token);
        var segundo = await AuthFlow.LogoutAsync(client, antesDoLogout, csrf, bearer: token);

        Assert.Equal(HttpStatusCode.NoContent, primeiro.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, segundo.StatusCode);
    }

    [Theory]
    [InlineData("/api/Auth/login")]
    [InlineData("/api/Auth/refresh")]
    [InlineData("/api/Auth/logout")]
    public async Task OperacoesComCookie_SemTokenCsrfValido_Retornam400(string path)
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var outroNavegador = new CookieJar();
        var csrfDeOutroPar = await AuthFlow.GetCsrfAsync(client, outroNavegador);
        var body = new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha };

        var semHeader = await AuthFlow.SendAsync(client, HttpMethod.Post, path, jar.Clone(), body: body);
        var parTrocado = await AuthFlow.SendAsync(client, HttpMethod.Post, path, jar.Clone(), csrfDeOutroPar, body: body);

        Assert.Equal(HttpStatusCode.BadRequest, semHeader.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, parTrocado.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/Auth/csrf")]
    [InlineData("POST", "/api/Auth/login")]
    [InlineData("POST", "/api/Auth/refresh")]
    [InlineData("POST", "/api/Auth/logout")]
    public async Task OperacoesComCookie_ForaDeHttps_Retornam400ProblemDetails(string method, string path)
    {
        using var factory = new AlmiranteApiFactory();
        using var http = factory.CreateManualCookieClient("http://localhost");

        var response = await AuthFlow.SendAsync(http, new HttpMethod(method), path, new CookieJar(), csrf: "x",
            body: method == "POST" ? new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha } : null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("HTTPS obrigatório.", problem.GetProperty("title").GetString());
        Assert.Empty(CookieJar.SetCookies(response));
    }
}
