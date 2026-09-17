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
    private const string SenhaPadraoTeste = "senha123";

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

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(AlmiranteApiFactory factory)
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
    // com o Role informado e devolve um HttpClient autenticado como esse usuário. Usado pelos
    // testes de autorização do lançamento geral, que exigem cobrir as 5 roles permitidas
    // (ADM, DIR, DIRA, SEC, TES) e ao menos uma role sem permissão.
    public static async Task<(HttpClient Client, Guid UsuarioId)> CreateAuthenticatedClientForRoleAsync(
        WebApplicationFactory<Program> factory, string role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>();

        var cargoId = await db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync();

        var email = $"teste-{role.ToLowerInvariant()}-{Guid.NewGuid():N}@local.dev";
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Nome = $"Usuário Teste {role}",
            Email = email,
            EmailNormalizado = email.ToUpperInvariant(),
            SenhaHash = string.Empty,
            CargoId = cargoId,
        };
        usuario.SenhaHash = hasher.HashPassword(usuario, SenhaPadraoTeste);

        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        await AddCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/Auth/login", new { email, senha = SenhaPadraoTeste });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token.AccessToken);

        return (client, usuario.Id);
    }

    // Adiciona um usuário "comum" (sem login usado nos testes) diretamente no banco, só para
    // testes que precisam de mais de um usuário elegível para o lançamento geral.
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
}
