using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>Sessões persistidas nas bordas do refresh e do logout: reuso, corrida entre dois refresh, cookie malformado e logout pela sessão.</summary>
public sealed class AuthSessoesBordasTests
{
    private const string CookieRefresh = "__Host-almirante-refresh";

    /// <summary>Antes do próximo SaveChanges, roda a ação (outra "requisição") e devolve o conflito de concorrência que o EF veria.</summary>
    private sealed class CorridaNoSave : SaveChangesInterceptor
    {
        private Func<Task>? _antes;

        public void ArmarConflito(Func<Task> antes) => _antes = antes;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _antes, null) is { } antes)
            {
                await antes();
                throw new DbUpdateConcurrencyException("outro refresh da mesma sessão gravou antes (simulado)");
            }
            return result;
        }
    }

    private sealed class FactoryComCorrida : AlmiranteApiFactory
    {
        private readonly string _banco = Guid.NewGuid().ToString();
        public CorridaNoSave Corrida { get; } = new();

        protected override void ConfigureDatabase(IServiceCollection services) =>
            services.AddDbContext<AlmiranteDbContext>(o => o.UseInMemoryDatabase(_banco).AddInterceptors(Corrida));
    }

    private static string Cookie(HttpResponseMessage resposta) =>
        resposta.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieRefresh + "=", StringComparison.Ordinal)).Split(';')[0][(CookieRefresh.Length + 1)..];

    private static async Task<(HttpClient Client, string Token, string Refresh)> LoginAsync(AlmiranteApiFactory factory)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        var csrf = await client.GetAsync("/api/Auth/csrf");
        var csrfCookie = csrf.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-almirante-csrf=")).Split(';')[0];
        var csrfToken = (await csrf.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("csrfToken").GetString()!;
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/login")
        {
            Content = JsonContent.Create(new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha }),
        };
        login.Headers.Add("Cookie", csrfCookie);
        login.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var resposta = await client.SendAsync(login);
        resposta.EnsureSuccessStatusCode();
        var token = (await resposta.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetProperty("accessToken").GetString()!;
        return (client, token, Cookie(resposta));
    }

    /// <summary>POST anônimo com CSRF novo (sem Bearer) e o cookie de refresh indicado.</summary>
    private static async Task<HttpResponseMessage> PostComRefreshAsync(HttpClient client, string rota, string? refresh, string? bearer = null)
    {
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Auth/csrf");
        if (bearer is not null) csrfRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var csrf = await client.SendAsync(csrfRequest);
        var csrfCookie = csrf.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-almirante-csrf=")).Split(';')[0];
        var csrfToken = (await csrf.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("csrfToken").GetString()!;
        var request = new HttpRequestMessage(HttpMethod.Post, rota);
        request.Headers.Add("Cookie", refresh is null ? csrfCookie : $"{csrfCookie}; {CookieRefresh}={refresh}");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private static Task<AuthSession> SessaoAsync(AlmiranteApiFactory factory) =>
        TestHelpers.WithDbAsync(factory, db => db.AuthSessions.AsNoTracking().OrderByDescending(s => s.CreatedAtUtc).FirstAsync());

    [Fact]
    public async Task ReusoDoRefreshDepoisDoLogout_NaoReescreveOMotivoDaRevogacao()
    {
        await using var factory = new AlmiranteApiFactory();
        var (client, _, primeiro) = await LoginAsync(factory);
        var renovado = await PostComRefreshAsync(client, "/api/Auth/refresh", primeiro);
        Assert.Equal(HttpStatusCode.OK, renovado.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostComRefreshAsync(client, "/api/Auth/logout", Cookie(renovado))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostComRefreshAsync(client, "/api/Auth/refresh", primeiro)).StatusCode);
        Assert.Equal("logout", (await SessaoAsync(factory)).RevocationReason);
    }

    [Fact]
    public async Task ReusoDoRefreshJaConsumido_RevogaASessaoInteira_ETokenDesconhecidoSoRecebe401()
    {
        await using var factory = new AlmiranteApiFactory();
        var (client, _, primeiro) = await LoginAsync(factory);
        var renovado = await PostComRefreshAsync(client, "/api/Auth/refresh", primeiro);
        Assert.Equal(HttpStatusCode.OK, renovado.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostComRefreshAsync(client, "/api/Auth/refresh", primeiro)).StatusCode);
        var sessao = await SessaoAsync(factory);
        Assert.Equal("refresh-token-reuse", sessao.RevocationReason);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostComRefreshAsync(client, "/api/Auth/refresh", Cookie(renovado))).StatusCode);

        var desconhecido = Microsoft.AspNetCore.WebUtilities.Base64UrlTextEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostComRefreshAsync(client, "/api/Auth/refresh", desconhecido)).StatusCode);
        Assert.Equal(1, await TestHelpers.WithDbAsync(factory, db => db.AuthSessions.CountAsync()));
    }

    [Fact]
    public async Task DoisRefreshAoMesmoTempo_ORetardatarioRevogaASessao()
    {
        await using var factory = new FactoryComCorrida();
        var (client, _, refresh) = await LoginAsync(factory);
        factory.Corrida.ArmarConflito(() => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostComRefreshAsync(client, "/api/Auth/refresh", refresh)).StatusCode);
        var sessao = await SessaoAsync(factory);
        Assert.Equal("concurrent-refresh", sessao.RevocationReason);
        Assert.NotNull(sessao.RevokedAtUtc);
    }

    [Fact]
    public async Task RefreshQuePerdeACorridaParaUmLogout_NaoSobrescreveARevogacao()
    {
        await using var factory = new FactoryComCorrida();
        var (client, _, refresh) = await LoginAsync(factory);
        factory.Corrida.ArmarConflito(() => TestHelpers.WithDbAsync(factory, async db =>
        {
            var sessao = await db.AuthSessions.SingleAsync();
            sessao.RevokedAtUtc = DateTime.UtcNow;
            sessao.RevocationReason = "logout";
            return await db.SaveChangesAsync();
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostComRefreshAsync(client, "/api/Auth/refresh", refresh)).StatusCode);
        Assert.Equal("logout", (await SessaoAsync(factory)).RevocationReason);
    }

    [Fact]
    public async Task LogoutComCookieMalformado_NaoFalha_ESemCookieRevogaPelaSessaoDoToken()
    {
        await using var factory = new AlmiranteApiFactory();
        var (client, token, _) = await LoginAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await PostComRefreshAsync(client, "/api/Auth/logout", "@@@nao-e-base64url")).StatusCode);
        Assert.Null((await SessaoAsync(factory)).RevokedAtUtc);

        Assert.Equal(HttpStatusCode.NoContent, (await PostComRefreshAsync(client, "/api/Auth/logout", null, bearer: token)).StatusCode);
        Assert.Equal("logout", (await SessaoAsync(factory)).RevocationReason);
    }
}
