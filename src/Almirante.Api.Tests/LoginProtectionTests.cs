using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

// Relógio controlável para o lockout (AuthService/LoginLockout usam TimeProvider).
public sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

// Proteção de POST /api/Auth/login diretamente na API (TestServer, sem nginx).
public class LoginProtectionTests
{
    private const string SenhaErrada = "senha-errada-123";

    private static ConfiguredApiFactory RateLimitFactory(int permits = 3, int windowSeconds = 2, string? trustedCidr = null) => new(overrides: new Dictionary<string, string?>
    {
        ["LoginProtection:RateLimit:PermitLimit"] = permits.ToString(),
        ["LoginProtection:RateLimit:WindowSeconds"] = windowSeconds.ToString(),
        ["ReverseProxy:TrustedNetworkCidr"] = trustedCidr,
    });

    private static async Task<HttpClient> ClientWithCsrfAsync(AlmiranteApiFactory factory)
    {
        var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        return client;
    }

    [Fact]
    public async Task RateLimit_EsgotaPorIp_Retorna429ComRetryAfter_EDepoisRecupera()
    {
        using var factory = RateLimitFactory(permits: 3, windowSeconds: 2);
        using var client = await ClientWithCsrfAsync(factory);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.10")).StatusCode);

        var bloqueada = await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha, "203.0.113.10");
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(int.Parse(Assert.Single(bloqueada.Headers.GetValues("Retry-After"))) is >= 1 and <= 2);
        Assert.Equal("application/problem+json", bloqueada.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Muitas tentativas de login.", (await bloqueada.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        // Outros endpoints não são afetados pelo limite do login.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/csrf")).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha, "203.0.113.10")).StatusCode);
    }

    [Fact]
    public async Task RateLimit_IsolaClientesPorIp()
    {
        using var factory = RateLimitFactory(permits: 2, windowSeconds: 60);
        using var client = await ClientWithCsrfAsync(factory);

        for (var i = 0; i < 2; i++) await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.20");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.20")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.21")).StatusCode);
    }

    [Fact]
    public async Task RateLimit_IgnoraXForwardedForDeOrigemNaoConfiavel()
    {
        // Proxy confiável configurado, mas a conexão vem de fora dele: variar o header não cria novos buckets.
        using var factory = RateLimitFactory(permits: 2, windowSeconds: 60, trustedCidr: "172.30.0.0/24");
        using var client = await ClientWithCsrfAsync(factory);

        for (var i = 0; i < 2; i++)
            await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.30", xForwardedFor: $"198.51.100.{i}");
        var forjada = await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.30", xForwardedFor: "198.51.100.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, forjada.StatusCode);
    }

    [Fact]
    public async Task RateLimit_SemProxyConfigurado_IgnoraXForwardedFor()
    {
        using var factory = RateLimitFactory(permits: 1, windowSeconds: 60);
        using var client = await ClientWithCsrfAsync(factory);

        await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.31", xForwardedFor: "198.51.100.1");
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, "203.0.113.31", xForwardedFor: "198.51.100.2")).StatusCode);
    }

    [Fact]
    public async Task RateLimit_AtrasDeProxyConfiavel_UsaIpEncaminhado()
    {
        using var factory = RateLimitFactory(permits: 2, windowSeconds: 60, trustedCidr: "172.30.0.0/24");
        using var client = await ClientWithCsrfAsync(factory);
        const string nginx = "172.30.0.5";

        for (var i = 0; i < 2; i++) await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, nginx, xForwardedFor: "198.51.100.10");
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, nginx, xForwardedFor: "198.51.100.10")).StatusCode);
        // Outro cliente atrás do mesmo proxy tem bucket próprio; valor forjado à esquerda é descartado (ForwardLimit=1).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, nginx, xForwardedFor: "198.51.100.10, 198.51.100.11")).StatusCode);
    }

    // ---- Lockout por conta ----

    private static (ConfiguredApiFactory Factory, MutableTimeProvider Clock) LockoutFactory(int maxFailures = 3)
    {
        var clock = new MutableTimeProvider();
        var factory = new ConfiguredApiFactory(
            overrides: new Dictionary<string, string?>
            {
                ["LoginProtection:Lockout:MaxFailedAttempts"] = maxFailures.ToString(),
                ["LoginProtection:Lockout:FailureWindowMinutes"] = "15",
                ["LoginProtection:Lockout:LockoutMinutes"] = "15",
            },
            services: s => s.AddSingleton<TimeProvider>(clock));
        return (factory, clock);
    }

    private static Task<(int Falhas, DateTime? BloqueadoAte)> EstadoAdminAsync(AlmiranteApiFactory factory) =>
        TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking()
            .Where(u => u.EmailNormalizado == AlmiranteApiFactory.AdminEmail.ToUpperInvariant())
            .Select(u => new ValueTuple<int, DateTime?>(u.FalhasLoginConsecutivas, u.LoginBloqueadoAteUtc)).SingleAsync());

    [Fact]
    public async Task Lockout_BloqueiaContaMesmoComSenhaCorreta_SemRevelarBloqueio_ERecuperaAposExpirar()
    {
        var (factory, clock) = LockoutFactory(maxFailures: 3);
        using var _ = factory;
        using var client = await ClientWithCsrfAsync(factory);

        HttpResponseMessage? ultimaFalha = null;
        for (var i = 0; i < 3; i++) ultimaFalha = await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada);
        var (falhas, bloqueadoAte) = await EstadoAdminAsync(factory);
        Assert.Equal(3, falhas);
        Assert.NotNull(bloqueadoAte);

        var bloqueada = await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var inexistente = await TestHelpers.PostLoginAsync(client, "naoexiste@local.dev", AlmiranteApiFactory.AdminSenha);
        Assert.Equal(HttpStatusCode.Unauthorized, bloqueada.StatusCode);
        var corpo = await TestHelpers.NormalizedProblemAsync(bloqueada);
        Assert.Equal(await TestHelpers.NormalizedProblemAsync(ultimaFalha!), corpo);
        Assert.Equal(await TestHelpers.NormalizedProblemAsync(inexistente), corpo);
        Assert.False(bloqueada.Headers.Contains("Retry-After"));

        // Tentativas durante o bloqueio não o prolongam.
        clock.Now = clock.Now.AddMinutes(10);
        await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada);
        Assert.Equal(bloqueadoAte, (await EstadoAdminAsync(factory)).BloqueadoAte);

        clock.Now = clock.Now.AddMinutes(6);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha)).StatusCode);
        Assert.Equal((0, (DateTime?)null), await EstadoAdminAsync(factory));
    }

    [Fact]
    public async Task Lockout_SucessoZeraContador_EFalhasForaDaJanelaReiniciam()
    {
        var (factory, clock) = LockoutFactory(maxFailures: 3);
        using var _ = factory;
        using var client = await ClientWithCsrfAsync(factory);

        for (var i = 0; i < 2; i++) await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha)).StatusCode);
        await TestHelpers.AddCsrfAsync(client);
        Assert.Equal(0, (await EstadoAdminAsync(factory)).Falhas);

        for (var i = 0; i < 2; i++) await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada);
        clock.Now = clock.Now.AddMinutes(16);
        for (var i = 0; i < 2; i++) await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada);
        Assert.Equal((2, (DateTime?)null), await EstadoAdminAsync(factory));
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha)).StatusCode);
    }

    [Fact]
    public async Task Lockout_ContaFalhasDistribuidasEntreVariosIps()
    {
        var (factory, _) = LockoutFactory(maxFailures: 3);
        using var __ = factory;
        using var client = await ClientWithCsrfAsync(factory);

        foreach (var ip in new[] { "203.0.113.41", "203.0.113.42", "203.0.113.43" })
            await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, SenhaErrada, ip);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha, "203.0.113.44")).StatusCode);
    }

    [Fact]
    public async Task Lockout_EmailInexistente_NaoCriaEstadoNemFalha()
    {
        var (factory, _) = LockoutFactory(maxFailures: 1);
        using var __ = factory;
        using var client = await ClientWithCsrfAsync(factory);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(client, "ninguem@local.dev", SenhaErrada)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha)).StatusCode);
    }
}
