using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

public class AuthTests
{
    [Fact]
    public async Task Login_ComCredenciaisValidas_RetornaTokenEUsuario()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = AlmiranteApiFactory.AdminEmail,
            senha = AlmiranteApiFactory.AdminSenha,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token.AccessToken));
        Assert.Equal(AlmiranteApiFactory.AdminEmail, body.Usuario.Email);
        Assert.Equal("Admin", body.Usuario.Roles);
    }

    [Fact]
    public async Task Login_ComSenhaInvalida_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

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
        var body = await response.Content.ReadFromJsonAsync<UsuarioDto>();
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
}
