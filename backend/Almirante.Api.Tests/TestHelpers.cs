using System.Net.Http.Headers;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

public static class TestHelpers
{
    // Gravada diretamente como hash (sem passar pela política de senha): representa credenciais
    // pré-existentes, que devem continuar autenticando mesmo sem cumprir a política atual.
    public const string SenhaPadraoTeste = "senha123";

    public static async Task<string> LoginAsAdminAsync(HttpClient client)
    {
        await AddCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = AlmiranteApiFactory.AdminEmail,
            senha = AlmiranteApiFactory.AdminSenha,
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token.AccessToken;
    }

    public static async Task AddCsrfAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/Auth/csrf");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("csrfToken").GetString());
    }

    // Envia uma tentativa de login (com CSRF válido já presente no client) e devolve a resposta crua.
    public static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string senha,
        string? remoteIp = null, string? xForwardedFor = null, string? xForwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login") { Content = JsonContent.Create(new { email, senha }) };
        if (remoteIp is not null) request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, remoteIp);
        if (xForwardedFor is not null) request.Headers.Add("X-Forwarded-For", xForwardedFor);
        if (xForwardedProto is not null) request.Headers.Add("X-Forwarded-Proto", xForwardedProto);
        return client.SendAsync(request);
    }

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var token = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // O antiforgery do ASP.NET Core vincula o token ao usuário autenticado no momento da emissão;
        // o CSRF obtido antes do login (anônimo) não valida em chamadas autenticadas como o logout.
        await AddCsrfAsync(client);
        return client;
    }

    // Cria (via seed de Cargos já existente, ver DbSeeder.SeedCargosAsync) um usuário de teste
    // com o Role informado e devolve um HttpClient autenticado como esse usuário.
    public static async Task<(HttpClient Client, Guid UsuarioId)> CreateAuthenticatedClientForRoleAsync(
        WebApplicationFactory<Program> factory, string role)
    {
        var usuario = await AddUsuarioAsync(factory, $"Usuário Teste {role}", role);

        var client = factory.CreateClient();
        await AddCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/Auth/login", new { email = usuario.Email, senha = SenhaPadraoTeste });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token.AccessToken);

        return (client, usuario.Id);
    }

    // Adiciona um usuário diretamente no banco com a senha SenhaPadraoTeste.
    public static async Task<Usuario> AddUsuarioAsync(WebApplicationFactory<Program> factory, string nome, string role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>();

        var cargoId = await db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync();

        var email = $"membro-{Guid.NewGuid():N}@local.dev";
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Nome = nome,
            Email = email,
            EmailNormalizado = email.ToUpperInvariant(),
            SenhaHash = string.Empty,
            CargoId = cargoId,
        };
        usuario.SenhaHash = hasher.HashPassword(usuario, SenhaPadraoTeste);

        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
        return usuario;
    }

    public static async Task<T> WithDbAsync<T>(WebApplicationFactory<Program> factory, Func<AlmiranteDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>());
    }

    // Remove campos que variam por requisição (traceId) para comparar corpos de erro.
    public static async Task<string> NormalizedProblemAsync(HttpResponseMessage response)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString();
    }
}
