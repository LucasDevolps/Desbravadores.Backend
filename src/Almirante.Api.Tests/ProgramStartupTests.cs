using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Almirante.Api.Tests;

/// <summary>Configurações de startup de <c>Program.cs</c> que mudam o comportamento da API (ou impedem que ela suba).</summary>
public sealed class ProgramStartupTests
{
    /// <summary>
    /// DataProtection:KeysPath e Cors:AllowedOrigins são lidos de builder.Configuration antes do Build(): só uma configuração de host
    /// (UseSetting, que chega ao Program como argumento) é vista nesse ponto, como as variáveis de ambiente de um deploy.
    /// </summary>
    private sealed class FactoryComConfiguracaoDeHost(IDictionary<string, string> configuracao) : AlmiranteApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            foreach (var (chave, valor) in configuracao) builder.UseSetting(chave, valor);
        }
    }

    private static string Achatar(Exception exception)
    {
        var texto = new StringBuilder();
        for (Exception? atual = exception; atual is not null; atual = atual.InnerException) texto.AppendLine(atual.Message);
        return texto.ToString();
    }

    [Fact]
    public async Task DataProtectionKeysPath_PersisteAsChavesDoAntiforgeryNaPastaConfigurada()
    {
        var pasta = Path.Combine(Path.GetTempPath(), "almirante-testes-dp", Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new FactoryComConfiguracaoDeHost(new Dictionary<string, string> { ["DataProtection:KeysPath"] = pasta });
            var resposta = await factory.CreateClient().GetAsync("/api/Auth/csrf");

            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var chave = Assert.Single(Directory.GetFiles(pasta, "key-*.xml"));
            Assert.Contains("<key ", await File.ReadAllTextAsync(chave));
        }
        finally
        {
            try { Directory.Delete(pasta, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("talvez")]
    [InlineData("1")]
    public void SwaggerEnabledInvalido_ImpedeOStartup(string valor)
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Swagger:Enabled"] = valor });
        var erro = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Swagger:Enabled deve ser true ou false.", Achatar(erro));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("721")]
    public void RotationHoursForaDaFaixa_ImpedeOStartup(string horas)
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["DbCredentials:RotationHours"] = horas });
        var erro = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("DbCredentials:RotationHours deve estar entre 1 e 720.", Achatar(erro));
    }

    [Fact]
    public async Task CorsAllowedOrigins_SubstituiAsOrigensPadrao()
    {
        await using var factory = new FactoryComConfiguracaoDeHost(new Dictionary<string, string> { ["Cors:AllowedOrigins:0"] = "https://almirante.exemplo.org" });
        var client = factory.CreateClient();

        async Task<string?> OrigemPermitidaAsync(string origem)
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/Auth/csrf");
            preflight.Headers.Add("Origin", origem);
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            using var resposta = await client.SendAsync(preflight);
            return resposta.Headers.TryGetValues("Access-Control-Allow-Origin", out var valores) ? valores.Single() : null;
        }

        Assert.Equal("https://almirante.exemplo.org", await OrigemPermitidaAsync("https://almirante.exemplo.org"));
        Assert.Null(await OrigemPermitidaAsync("http://localhost:4200"));
    }

    [Fact]
    public async Task TokenSemKid_EhRecusadoMesmoAssinadoComAChaveCorreta()
    {
        await using var factory = new AlmiranteApiFactory();
        var client = factory.CreateClient();
        var token = await TestHelpers.LoginAsAdminAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);

        // Mesmo payload e mesma chave, só sem o "kid" no cabeçalho: a chave não é resolvida e a assinatura não é aceita.
        var payload = token.Split('.')[1];
        var cabecalho = Base64UrlEncoder.Encode("""{"alg":"HS256","typ":"at+jwt"}""");
        var assinatura = Base64UrlEncoder.Encode(HMACSHA256.HashData(Convert.FromBase64String(factory.JwtKeyBase64), Encoding.ASCII.GetBytes($"{cabecalho}.{payload}")));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{cabecalho}.{payload}.{assinatura}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/Me")).StatusCode);
    }
}
