using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Almirante.Api.Security;

namespace Almirante.Api.Tests;

public class SecurityHeadersTests
{
    private static void AssertCabecalhosBasicos(HttpResponseMessage response, string? csp = null)
    {
        string Header(string name) => Assert.Single(response.Headers.GetValues(name));
        Assert.Equal("nosniff", Header("X-Content-Type-Options"));
        Assert.Equal("DENY", Header("X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header("Referrer-Policy"));
        Assert.Equal(csp ?? SecurityHeadersMiddleware.ApiContentSecurityPolicy, Header("Content-Security-Policy"));
    }

    private static async Task<Dictionary<string, HttpResponseMessage>> RespostasDeTodosOsTiposAsync(AlmiranteApiFactory factory, HttpClient anonymous)
    {
        var (semPermissao, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        await TestHelpers.AddCsrfAsync(anonymous);
        var respostas = new Dictionary<string, HttpResponseMessage>
        {
            ["200"] = await anonymous.GetAsync("/api/Auth/csrf"),
            ["401"] = await anonymous.GetAsync("/api/Usuarios"),
            ["401-login"] = await TestHelpers.PostLoginAsync(anonymous, AlmiranteApiFactory.AdminEmail, "senha-errada-123", "203.0.113.90"),
            ["403"] = await semPermissao.GetAsync("/api/Usuarios"),
            ["404"] = await anonymous.GetAsync("/api/rota-inexistente"),
            ["400-validacao"] = await admin.PostAsJsonAsync("/api/Lancamentos/Registrar", new { finalidade = "x", categoria = "y", valor = 0 }),
            ["500"] = await anonymous.GetAsync(AlmiranteApiFactory.ThrowPath),
        };
        await TestHelpers.PostLoginAsync(anonymous, AlmiranteApiFactory.AdminEmail, "senha-errada-123", "203.0.113.90");
        respostas["429"] = await TestHelpers.PostLoginAsync(anonymous, AlmiranteApiFactory.AdminEmail, "senha-errada-123", "203.0.113.90");
        return respostas;
    }

    private static Dictionary<string, string?> DoisLoginsPorIp => new() { ["LoginProtection:RateLimit:PermitLimit"] = "2" };

    [Fact]
    public async Task Development_TodasAsRespostasTemCabecalhos_ESemHsts()
    {
        using var factory = new ConfiguredApiFactory("Development", DoisLoginsPorIp);
        factory.ClientOptions.BaseAddress = new Uri("https://api.almirante.test");
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://api.almirante.test") });

        var respostas = await RespostasDeTodosOsTiposAsync(factory, client);

        Assert.Equal(HttpStatusCode.OK, respostas["200"].StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, respostas["401"].StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, respostas["401-login"].StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, respostas["403"].StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, respostas["404"].StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, respostas["400-validacao"].StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, respostas["500"].StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, respostas["429"].StatusCode);
        foreach (var (tipo, response) in respostas)
        {
            AssertCabecalhosBasicos(response);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"), $"HSTS não deve existir em Development ({tipo}).");
        }
    }

    [Fact]
    public async Task Producao_Https_EmiteHstsEmTodasAsRespostas_InclusiveErros()
    {
        using var factory = new ConfiguredApiFactory("Production", DoisLoginsPorIp);
        factory.ClientOptions.BaseAddress = new Uri("https://api.almirante.test");
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://api.almirante.test") });

        foreach (var (_, response) in await RespostasDeTodosOsTiposAsync(factory, client))
        {
            AssertCabecalhosBasicos(response);
            Assert.Equal("max-age=31536000", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
        }
    }

    [Fact]
    public async Task Producao_HttpOuHostExcluido_NaoEmiteHsts()
    {
        using var factory = new ConfiguredApiFactory("Production");
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("http://api.almirante.test"), AllowAutoRedirect = false });
        using var localhost = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var viaHttp = await http.GetAsync("/api/Auth/csrf");
        AssertCabecalhosBasicos(viaHttp);
        Assert.False(viaHttp.Headers.Contains("Strict-Transport-Security"));
        // Sem porta HTTPS configurada não há redirecionamento (nem loop) — nginx/IIS terminam TLS.
        Assert.NotEqual(HttpStatusCode.RedirectKeepVerb, viaHttp.StatusCode);
        Assert.NotEqual(HttpStatusCode.TemporaryRedirect, viaHttp.StatusCode);

        Assert.False((await localhost.GetAsync("/api/Auth/csrf")).Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("172.30.0.5", true)]     // proxy confiável informou HTTPS
    [InlineData("203.0.113.77", false)]  // cliente direto tentando forjar X-Forwarded-Proto
    public async Task Producao_AtrasDeProxy_HstsSoConfiaEmXForwardedProtoDoProxyConfiavel(string conexao, bool esperaHsts)
    {
        using var factory = new ConfiguredApiFactory("Production", new Dictionary<string, string?> { ["ReverseProxy:TrustedNetworkCidr"] = "172.30.0.0/24" });
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://api.almirante.test"), AllowAutoRedirect = false });

        // /alive: /api/Auth/csrf exige HTTPS (cookie antiforgery Secure) e responderia 500 via HTTP.
        var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, conexao);
        request.Headers.Add("X-Forwarded-For", "198.51.100.7");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCabecalhosBasicos(response);
        Assert.Equal(esperaHsts, response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Swagger_FuncionaComCspPropria_SemScriptsOuEstilosInline()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var index = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);
        AssertCabecalhosBasicos(index, SecurityHeadersMiddleware.SwaggerContentSecurityPolicy);

        // A CSP do Swagger não permite script inline; a página servida também não traz estilo inline
        // (o swagger-ui só injeta estilo em runtime, coberto por style-src 'unsafe-inline').
        var html = await index.Content.ReadAsStringAsync();
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>\s*\S", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"<style[\s>]|\sstyle\s*=|\son[a-z]+\s*=", RegexOptions.IgnoreCase), html);
        foreach (Match asset in Regex.Matches(html, @"(?:src|href)=""(?!https?:|data:|//)([^""]+)"""))
        {
            var response = await client.GetAsync($"/swagger/{asset.Groups[1].Value.TrimStart('.', '/')}");
            Assert.True(response.IsSuccessStatusCode, $"Recurso do Swagger indisponível: {asset.Groups[1].Value}");
        }

        var documento = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, documento.StatusCode);
        AssertCabecalhosBasicos(documento, SecurityHeadersMiddleware.SwaggerContentSecurityPolicy);
        Assert.Contains("/api/Auth/login", await documento.Content.ReadAsStringAsync());
    }
}
