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
    public const string SenhaPadraoTeste = "senha123";

    /// <summary>Ontem em UTC: nunca é futura no fuso do clube, qualquer que seja a hora da execução.</summary>
    public static DateOnly DataPagamentoPassada => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    public static Task<HttpResponseMessage> RegistrarPagamentoAsync(HttpClient client, Guid lancamentoId, decimal valorPago, DateOnly? dataPagamento = null) =>
        client.PostAsJsonAsync($"/api/Lancamentos/{lancamentoId}/Pagamento",
            new { dataPagamento = (dataPagamento ?? DataPagamentoPassada).ToString("yyyy-MM-dd"), valorPago });

    /// <summary>Registra o pagamento integral (valorPago = valor atual); lançamento inexistente/inativo chega à API e recebe 404.</summary>
    public static async Task<HttpResponseMessage> PagarAsync(HttpClient client, Guid lancamentoId)
    {
        using var atual = await client.GetAsync($"/api/Lancamentos/{lancamentoId}");
        var valor = atual.IsSuccessStatusCode
            ? (await atual.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("valor").GetDecimal()
            : 0.01m;
        return await RegistrarPagamentoAsync(client, lancamentoId, valor);
    }

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
        await AddCsrfAsync(client);
        return client;
    }

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

    public static Task InserirUsuarioLegadoAsync(AlmiranteDbContext db, Usuario u) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas)
            VALUES ({u.Id}, {u.Nome}, {u.Email}, {u.EmailNormalizado}, {u.SenhaHash}, {u.CargoId}, {u.DataCriacao}, {u.SecurityVersion}, {u.FalhasLoginConsecutivas})
            """);

    public static async Task<T> WithDbAsync<T>(WebApplicationFactory<Program> factory, Func<AlmiranteDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>());
    }

    public static async Task<string> NormalizedProblemAsync(HttpResponseMessage response)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString();
    }

    public static string NovoCpf(bool comZeroInicial = true)
    {
        while (true)
        {
            var digitos = new int[11];
            for (var i = comZeroInicial ? 1 : 0; i < 9; i++) digitos[i] = Random.Shared.Next(10);
            if (digitos.Take(9).Distinct().Count() == 1) continue;
            digitos[9] = DigitoVerificador(digitos, 9);
            digitos[10] = DigitoVerificador(digitos, 10);
            return string.Concat(digitos);
        }

        static int DigitoVerificador(int[] d, int quantidade)
        {
            var soma = 0;
            for (var i = 0; i < quantidade; i++) soma += d[i] * (quantidade + 1 - i);
            var resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }

    public const string RgFicticio = "12.345.678-9";

    public static string MascararCpf(string cpf) => $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}";
}
