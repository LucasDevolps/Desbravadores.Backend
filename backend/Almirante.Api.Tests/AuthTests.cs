using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

public class AuthTests
{
    [Fact]
    public async Task Login_ComCredenciaisValidas_RetornaTokenEUsuario()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = AlmiranteApiFactory.AdminEmail,
            senha = AlmiranteApiFactory.AdminSenha,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token.AccessToken));
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("usuario", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_ComSenhaInvalida_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = AlmiranteApiFactory.AdminEmail,
            senha = "senha-errada",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_ComEmailInexistente_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = "naoexiste@local.dev",
            senha = "qualquer",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_ComPayloadInvalido_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new { email = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Me_SemToken_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Auth/Me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_ComToken_RetornaUsuarioAutenticado()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Auth/Me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal(AlmiranteApiFactory.AdminEmail, body!.Email);
    }

    [Fact]
    public async Task Usuarios_SemToken_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Usuarios");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Usuarios_ComToken_RetornaListaComAdminSeedado()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Usuarios");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<UsuarioListItemDto>>();
        Assert.NotNull(body);
        Assert.Contains(body!, u => u.Email == AlmiranteApiFactory.AdminEmail);
    }

    [Fact]
    public async Task Login_ERefresh_ExpoemSomenteEnvelopeToken_EJwtMinimo()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var login = await client.PostAsJsonAsync("/api/Auth/login", new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha });
        login.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal(["token"], document.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Equal(["accessToken", "expiresAtUtc"], document.RootElement.GetProperty("token").EnumerateObject().Select(x => x.Name).ToArray());
        var raw = document.RootElement.GetProperty("token").GetProperty("accessToken").GetString()!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(raw);
        Assert.Equal("at+jwt", jwt.Header.Typ);
        Assert.Equal(["aud", "exp", "iat", "iss", "jti", "nbf", "role", "sid", "sub"], jwt.Claims.Select(x => x.Type).Distinct().Order().ToArray());
        Assert.DoesNotContain(jwt.Claims, x => x.Type is "email" or "name");

        var refresh = await client.PostAsync("/api/Auth/refresh", null);
        refresh.EnsureSuccessStatusCode();
        using var refreshed = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        Assert.Equal(["token"], refreshed.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Logout_RevogaSessao_EBearerAnteriorDeixaDeAutorizar()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/Auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/Me")).StatusCode);
    }
}
