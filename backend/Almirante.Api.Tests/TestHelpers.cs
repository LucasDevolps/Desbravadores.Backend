using System.Net.Http.Headers;
using System.Net.Http.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

public static class TestHelpers
{
    public static async Task<string> LoginAsAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = AlmiranteApiFactory.AdminEmail,
            senha = AlmiranteApiFactory.AdminSenha,
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token.AccessToken;
    }

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(AlmiranteApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
