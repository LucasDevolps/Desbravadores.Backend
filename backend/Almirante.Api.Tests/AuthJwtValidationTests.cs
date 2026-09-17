using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Almirante.Api.Tests;

public sealed class JwtValidationFixture : IAsyncLifetime
{
    public AlmiranteApiFactory Factory { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public string AdminToken { get; private set; } = null!;
    public string AdminLoginJson { get; private set; } = null!;
    public string MembroToken { get; private set; } = null!;
    public string AdminRefreshCookie { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Client = Factory.CreateManualCookieClient();
        var jar = new CookieJar();
        var csrf = await AuthFlow.GetCsrfAsync(Client, jar);
        var login = await AuthFlow.SendAsync(Client, HttpMethod.Post, "/api/Auth/login", jar, csrf,
            body: new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha });
        AdminLoginJson = await login.Content.ReadAsStringAsync();
        AdminToken = JsonDocument.Parse(AdminLoginJson).RootElement.GetProperty("token").GetProperty("accessToken").GetString()!;
        AdminRefreshCookie = jar[CookieJar.RefreshCookie]!;
        var (membro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, "DS");
        MembroToken = membro.DefaultRequestHeaders.Authorization!.Parameter!;
        membro.Dispose();
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Factory.DisposeAsync().AsTask();
    }
}

// Perfil estrito do access token (docs/authentication-security.md). Cada caso usa a validação real da
// API; os tokens negativos são assinados com a chave de teste para isolar a regra verificada.
public class AuthJwtValidationTests(JwtValidationFixture fixture) : IClassFixture<JwtValidationFixture>
{
    private static long Agora => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task<HttpStatusCode> MeAsync(string token, string path = "/api/Auth/Me")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await fixture.Client.SendAsync(request);
        return response.StatusCode;
    }

    private string Admin(Action<Dictionary<string, object?>>? payload = null, Action<Dictionary<string, object?>>? header = null,
        string alg = "HS256", byte[]? key = null) => TestTokens.Resign(fixture.AdminToken, payload, header, alg, key);

    private static string PayloadJson(string token) => Base64UrlEncoder.Decode(new JsonWebToken(token).EncodedPayload);

    private static string HeaderJson(string token) => Base64UrlEncoder.Decode(new JsonWebToken(token).EncodedHeader);

    [Fact]
    public async Task Controle_TokenReassinadoComAChaveCorreta_EhAceito() =>
        Assert.Equal(HttpStatusCode.OK, await MeAsync(Admin()));

    [Fact]
    public async Task AssinaturaAdulterada_EhRecusada()
    {
        var partes = fixture.AdminToken.Split('.');
        var assinatura = partes[2].ToCharArray();
        assinatura[5] = assinatura[5] == 'A' ? 'B' : 'A';
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync($"{partes[0]}.{partes[1]}.{new string(assinatura)}"));
    }

    [Fact]
    public async Task PayloadAlteradoSemReassinar_EhRecusado()
    {
        var partes = fixture.AdminToken.Split('.');
        var payload = PayloadJson(fixture.AdminToken).Replace("\"ADM\"", "\"DIR\"");
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync($"{partes[0]}.{Base64UrlEncoder.Encode(payload)}.{partes[2]}"));
    }

    [Fact]
    public async Task AlgNone_EhRecusado()
    {
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"kid\":\"test-v1\",\"typ\":\"at+jwt\"}");
        var payload = new JsonWebToken(fixture.AdminToken).EncodedPayload;
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync($"{header}.{payload}."));
    }

    [Theory]
    [InlineData("HS512")]
    [InlineData("RS256")]
    public async Task AlgoritmoInesperado_EhRecusado(string algoritmo) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(alg: algoritmo)));

    [Theory]
    [InlineData("iss", "Outro.Emissor")]
    [InlineData("aud", "Outra.Api")]
    public async Task EmissorOuAudienciaErrados_SaoRecusados(string claim, string valor) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(p => p[claim] = valor)));

    [Theory]
    [InlineData("JWT")]
    [InlineData("rt+jwt")]
    [InlineData(null)]
    public async Task TipoDiferenteDeAtJwt_EhRecusado(string? typ) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(header: h => { if (typ is null) h.Remove("typ"); else h["typ"] = typ; })));

    [Theory]
    [InlineData("exp")]
    [InlineData("iat")]
    [InlineData("nbf")]
    [InlineData("jti")]
    [InlineData("sid")]
    [InlineData("sub")]
    [InlineData("role")]
    public async Task ClaimObrigatoriaAusente_EhRecusada(string claim) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(p => p.Remove(claim))));

    [Theory]
    [InlineData(10, HttpStatusCode.OK)]
    [InlineData(120, HttpStatusCode.Unauthorized)]
    public async Task IatNoFuturo_RespeitaTolerancia(int segundos, HttpStatusCode esperado) =>
        Assert.Equal(esperado, await MeAsync(Admin(p => { p["iat"] = Agora + segundos; p["nbf"] = Agora - 5; p["exp"] = Agora + 600; })));

    [Theory]
    [InlineData(-15, HttpStatusCode.OK)]
    [InlineData(-60, HttpStatusCode.Unauthorized)]
    public async Task ExpVencido_RespeitaClockSkewDe30Segundos(int segundos, HttpStatusCode esperado) =>
        Assert.Equal(esperado, await MeAsync(Admin(p => { p["iat"] = Agora - 600; p["nbf"] = Agora - 600; p["exp"] = Agora + segundos; })));

    [Theory]
    [InlineData(15, HttpStatusCode.OK)]
    [InlineData(90, HttpStatusCode.Unauthorized)]
    public async Task NbfNoFuturo_RespeitaClockSkewDe30Segundos(int segundos, HttpStatusCode esperado) =>
        Assert.Equal(esperado, await MeAsync(Admin(p => { p["iat"] = Agora - 5; p["nbf"] = Agora + segundos; p["exp"] = Agora + 600; })));

    [Fact]
    public async Task NbfPosteriorAExp_EhRecusado() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(p => { p["iat"] = Agora - 5; p["nbf"] = Agora + 20; p["exp"] = Agora + 15; })));

    [Theory]
    [InlineData("exp")]
    [InlineData("iat")]
    [InlineData("nbf")]
    public async Task DataQueNaoEhNumericDate_EhRecusada(string claim) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(p => p[claim] = JsonSerializer.SerializeToElement(p[claim]).ToString())));

    [Fact]
    public async Task SubQueNaoEhGuid_EhRecusado() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(p => p["sub"] = "admin")));

    [Theory]
    [InlineData("role")]
    [InlineData("sid")]
    public async Task ClaimCriticaComoArray_EhRecusada(string claim) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(p => p[claim] = new[] { p[claim]?.ToString(), p[claim]?.ToString() })));

    [Theory]
    [InlineData("v9")]
    [InlineData("../../appsettings.json")]
    [InlineData(null)]
    public async Task KidDesconhecidoOuAusente_EhRecusado(string? kid) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(Admin(header: h => { if (kid is null) h.Remove("kid"); else h["kid"] = kid; })));

    [Fact]
    public async Task ChaveEmbutidaOuUrlDeChavesNoHeader_SaoIgnoradas()
    {
        var chaveDoAtacante = Encoding.UTF8.GetBytes("chave-do-atacante-com-mais-de-32-bytes!!");
        var token = Admin(header: h =>
        {
            h["jku"] = "https://atacante.example/jwks";
            h["jwk"] = new { kty = "oct", k = Base64UrlEncoder.Encode(chaveDoAtacante) };
        }, key: chaveDoAtacante);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(token));
    }

    [Fact]
    public async Task RefreshTokenUsadoComoBearer_EhRecusado() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(fixture.AdminRefreshCookie));

    [Theory]
    [InlineData("sub", true)]
    [InlineData("sub", false)]
    [InlineData("sid", false)]
    [InlineData("exp", false)]
    [InlineData("role", false)]
    public async Task ClaimCriticaDuplicada_EhRecusada(string claim, bool duplicataPrimeiro)
    {
        var payload = PayloadJson(fixture.AdminToken);
        var valor = claim switch
        {
            "exp" => (Agora + 3600).ToString(),
            "role" => "\"ADM\"",
            _ => $"\"{Guid.NewGuid()}\"",
        };
        var duplicado = duplicataPrimeiro
            ? "{" + $"\"{claim}\":{valor}," + payload[1..]
            : payload[..^1] + $",\"{claim}\":{valor}" + "}";

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(TestTokens.SignRaw(HeaderJson(fixture.AdminToken), duplicado)));
    }

    [Fact]
    public async Task RoleDuplicadaNaoConcedePermissao()
    {
        var payload = PayloadJson(fixture.MembroToken);
        var token = TestTokens.SignRaw(HeaderJson(fixture.MembroToken), payload[..^1] + ",\"role\":\"ADM\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(token, "/api/Lancamentos"));
    }

    [Fact]
    public async Task TokenAcimaDoLimiteDeTamanho_EhRecusado()
    {
        var token = Admin(p => p["pad"] = new string('x', 9000));
        Assert.True(Encoding.ASCII.GetByteCount(token) > 8192);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(token));
    }

    [Fact]
    public void ExpiresAtUtc_RepresentaExatamenteOExpDoToken()
    {
        var token = JsonDocument.Parse(fixture.AdminLoginJson).RootElement.GetProperty("token");
        var expiresAtUtc = token.GetProperty("expiresAtUtc").GetDateTimeOffset();
        var exp = new JsonWebToken(fixture.AdminToken).ValidTo;

        Assert.Equal(TimeSpan.Zero, expiresAtUtc.Offset);
        Assert.Equal(new DateTimeOffset(exp, TimeSpan.Zero), expiresAtUtc);
    }
}
