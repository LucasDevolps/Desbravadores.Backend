using System.Net;
using System.Security.Cryptography;
using System.Text;
using Almirante.Api.Data;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Almirante.Api.Tests;

public class AuthSessionLifecycleTests
{
    private static DateTimeOffset CookieExpires(HttpResponseMessage response)
    {
        var setCookie = CookieJar.SetCookies(response).Single(c => c.StartsWith(CookieJar.RefreshCookie + "=", StringComparison.Ordinal));
        return DateTimeOffset.Parse(CookieJar.Parse(setCookie).Attributes["expires"]!);
    }

    private static async Task<DateTime> ExpiracaoDoRefreshVigenteAsync(AlmiranteApiFactory factory, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        return await db.RefreshTokens.Where(t => t.SessionId == sessionId && t.ConsumedAtUtc == null).Select(t => t.ExpiresAtUtc).SingleAsync();
    }

    private static void AssertMesmoInstante(DateTimeOffset esperado, DateTimeOffset atual) =>
        Assert.True(Math.Abs((esperado - atual).TotalSeconds) < 1, $"esperado {esperado:O}, obtido {atual:O}");

    [Fact]
    public async Task CookieDeRefresh_ExpiraNoLimiteDeInatividade_EmUtc_NoLoginENaRenovacao()
    {
        using var factory = new InstrumentedApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var csrf = await AuthFlow.GetCsrfAsync(client, jar);

        var login = await AuthFlow.SendAsync(client, HttpMethod.Post, "/api/Auth/login", jar, csrf,
            body: new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha });
        var sid = AuthFlow.SessionId(await AuthFlow.ReadAccessTokenAsync(login));
        AssertMesmoInstante(factory.Clock.GetUtcNow().AddHours(24), CookieExpires(login));
        AssertMesmoInstante(new DateTimeOffset(DateTime.SpecifyKind(await ExpiracaoDoRefreshVigenteAsync(factory, sid), DateTimeKind.Utc)), CookieExpires(login));

        factory.Clock.Advance(TimeSpan.FromHours(5));
        var refresh = await AuthFlow.RefreshAsync(client, jar, csrf);

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        AssertMesmoInstante(factory.Clock.GetUtcNow().AddHours(24), CookieExpires(refresh));
    }

    [Theory]
    [InlineData(23, HttpStatusCode.OK)]
    [InlineData(25, HttpStatusCode.Unauthorized)]
    public async Task Refresh_RespeitaInatividadeDe24HorasDesdeAUltimaRenovacao(int horas, HttpStatusCode esperado)
    {
        using var factory = new InstrumentedApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var csrf = await AuthFlow.GetCsrfAsync(client, jar);

        factory.Clock.Advance(TimeSpan.FromHours(horas));

        Assert.Equal(esperado, (await AuthFlow.RefreshAsync(client, jar, csrf)).StatusCode);
    }

    [Fact]
    public async Task RenovacoesFrequentes_NaoEstendemOPrazoAbsolutoDe7Dias_NemNoCookieNemNoAccessToken()
    {
        using var factory = new InstrumentedApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var csrf = await AuthFlow.GetCsrfAsync(client, jar);
        var absoluto = factory.Clock.GetUtcNow().AddDays(7);

        HttpResponseMessage ultima = null!;
        for (var i = 0; i < 10; i++) // 10 × 20 h = 200 h > 168 h
        {
            factory.Clock.Advance(TimeSpan.FromHours(20));
            var resposta = await AuthFlow.RefreshAsync(client, jar, csrf);
            if (resposta.StatusCode != HttpStatusCode.OK)
            {
                Assert.True(factory.Clock.GetUtcNow() >= absoluto, "refresh recusado antes do prazo absoluto");
                Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
                return;
            }

            ultima = resposta;
            Assert.True(CookieExpires(resposta) <= absoluto.AddSeconds(1), "cookie ultrapassa o prazo absoluto");
            var exp = new JsonWebToken(await AuthFlow.ReadAccessTokenAsync(resposta)).ValidTo;
            Assert.True(new DateTimeOffset(exp, TimeSpan.Zero) <= absoluto, "access token ultrapassa o prazo absoluto");
        }

        Assert.Fail($"refresh continuou aceito após o prazo absoluto; última resposta {ultima.StatusCode}");
    }

    [Fact]
    public async Task Login_ComEmailInexistenteOuCargoInativo_VerificaSenhaComoNoCasoValido()
    {
        using var factory = new InstrumentedApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var csrf = await AuthFlow.GetCsrfAsync(client, jar);
        var inativo = await TestHelpers.AddUsuarioAsync(factory, "Cargo Inativo", "DS");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
            var cargo = await db.Cargos.SingleAsync(c => c.Role == "DS");
            cargo.Ativo = false;
            await db.SaveChangesAsync();
        }

        var antes = factory.Hasher.Verifications;
        var inexistente = await AuthFlow.SendAsync(client, HttpMethod.Post, "/api/Auth/login", jar, csrf, body: new { email = "ninguem@local.dev", senha = "qualquer" });
        var depoisInexistente = factory.Hasher.Verifications;
        var cargoInativo = await AuthFlow.SendAsync(client, HttpMethod.Post, "/api/Auth/login", jar, csrf, body: new { email = inativo.Email, senha = "senha123" });

        Assert.Equal(HttpStatusCode.Unauthorized, inexistente.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cargoInativo.StatusCode);
        Assert.Equal(await inexistente.Content.ReadAsStringAsync(), await cargoInativo.Content.ReadAsStringAsync());
        Assert.Equal(1, depoisInexistente - antes);
        Assert.Equal(1, factory.Hasher.Verifications - depoisInexistente);
    }

    public static TheoryData<string, string> ChavesInvalidas => new()
    {
        { "32 bytes zerados", Convert.ToBase64String(new byte[32]) },
        { "placeholder antigo em Base64", Convert.ToBase64String(Encoding.UTF8.GetBytes("troque-esta-chave-por-uma-chave-secreta-de-32-ou-mais-caracteres")) },
        { "frase legível com 40 caracteres", Convert.ToBase64String(Encoding.UTF8.GetBytes("minha senha super secreta do almirante!!")) },
        { "16 bytes aleatórios", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) },
        { "placeholder do .env.example", "BASE64_DE_32_BYTES_OU_MAIS" },
    };

    [Theory]
    [MemberData(nameof(ChavesInvalidas))]
    public void ChaveDeAssinatura_SemEntropiaOuFormatoValido_EhRecusada(string caso, string chave)
    {
        var options = new JwtOptions { Issuer = "i", Audience = "a", ActiveKeyId = "v1", Keys = new() { ["v1"] = chave } };
        Assert.Throws<OptionsValidationException>(() => JwtKeySet.GetKey(options, "v1"));
        Assert.False(string.IsNullOrEmpty(caso));
    }

    [Fact]
    public void ChaveDeAssinatura_Com32BytesAleatorios_EhAceita()
    {
        var options = new JwtOptions { Issuer = "i", Audience = "a", ActiveKeyId = "v1", Keys = new() { ["v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) } };
        Assert.Equal(32, JwtKeySet.GetKey(options, "v1").Key.Length);
    }

    [Fact]
    public async Task EventosDeSeguranca_SaoRegistrados_SemMaterialSecreto()
    {
        using var factory = new InstrumentedApiFactory();
        using var client = factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var csrf = await AuthFlow.GetCsrfAsync(client, jar);
        await AuthFlow.SendAsync(client, HttpMethod.Post, "/api/Auth/login", jar, csrf, body: new { email = AlmiranteApiFactory.AdminEmail, senha = "senha-errada" });
        var token = await AuthFlow.LoginAsync(client, jar, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);
        var refreshConsumido = jar.Clone();
        var valorDoRefreshConsumido = refreshConsumido[CookieJar.RefreshCookie]!;
        var rotacao = await AuthFlow.RefreshAsync(client, jar, csrf);
        await AuthFlow.RefreshAsync(client, refreshConsumido, csrf);
        await AuthFlow.LogoutAsync(client, jar, csrf);
        var sid = AuthFlow.SessionId(token).ToString();

        var auth = factory.Logs.Entries.Where(e => e.Category == "Almirante.Api.Services.AuthService").ToList();
        Assert.Contains(auth, e => e.EventId.Name == "LoginFalhou");
        Assert.Contains(auth, e => e.EventId.Name == "LoginRealizado" && e.Message.Contains(sid));
        Assert.Contains(auth, e => e.EventId.Name == "ReusoDeRefreshDetectado" && e.Level == LogLevel.Warning && e.Message.Contains(sid));
        var segredos = new[] { token, await AuthFlow.ReadAccessTokenAsync(rotacao), valorDoRefreshConsumido, csrf, AlmiranteApiFactory.AdminSenha, "senha-errada" };
        Assert.DoesNotContain(factory.Logs.Entries, e => segredos.Any(s => e.Message.Contains(s, StringComparison.Ordinal)));
    }
}
