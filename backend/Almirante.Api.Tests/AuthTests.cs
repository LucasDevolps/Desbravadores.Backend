using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Almirante.Api.Tests;

public class AuthTests
{
    [Fact]
    public async Task Login_ComCredenciaisValidas_RetornaTokenEUsuario()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = AlmiranteApiFactory.AdminEmail,
            senha = AlmiranteApiFactory.AdminSenha,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token.AccessToken));
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("usuario", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_ComSenhaInvalida_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = AlmiranteApiFactory.AdminEmail,
            senha = "senha-errada",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_ComEmailInexistente_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = "naoexiste@local.dev",
            senha = "qualquer",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_Falhas_SaoIndistinguiveis_EntreEmailInexistenteESenhaErrada()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var senhaErrada = await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, "senha-errada-123");
        var inexistente = await TestHelpers.PostLoginAsync(client, "naoexiste@local.dev", "senha-errada-123");

        Assert.Equal(HttpStatusCode.Unauthorized, senhaErrada.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inexistente.StatusCode);
        Assert.Equal(await TestHelpers.NormalizedProblemAsync(senhaErrada), await TestHelpers.NormalizedProblemAsync(inexistente));
        Assert.Equal(senhaErrada.Content.Headers.ContentType?.MediaType, inexistente.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_CredencialExistenteForaDaPoliticaAtual_ContinuaAutenticando()
    {
        // "senha123" não cumpre a política atual, mas foi definida antes dela: o login só verifica o hash.
        using var factory = new AlmiranteApiFactory();
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);
    }

    [Fact]
    public async Task Login_ComPayloadInvalido_Retorna400()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var response = await client.PostAsJsonAsync("/api/Auth/login", new { email = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginEMe_RespondemComCacheControlNoStore_InclusiveEmFalhas()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        var falha = await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, "senha-errada-123");
        var meSemToken = await client.GetAsync("/api/Auth/Me");
        var token = await TestHelpers.LoginAsAdminAsync(client);
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/Auth/Me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var meOk = await client.SendAsync(me);
        await TestHelpers.AddCsrfAsync(client);
        var loginOk = await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, AlmiranteApiFactory.AdminSenha);

        foreach (var response in new[] { falha, meSemToken, meOk, loginOk })
        {
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
        }
        Assert.Equal(HttpStatusCode.OK, meOk.StatusCode);
        Assert.Equal(HttpStatusCode.OK, loginOk.StatusCode);
    }

    [Fact]
    public async Task Me_SemToken_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Auth/Me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_ComToken_RetornaUsuarioAutenticado_NoDtoMinimo()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Auth/Me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["cargo", "email", "id", "nome"], document.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(["id", "nome", "role"], document.RootElement.GetProperty("cargo").EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(AlmiranteApiFactory.AdminEmail, document.RootElement.GetProperty("email").GetString());
        Assert.Equal("ADM", document.RootElement.GetProperty("cargo").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Me_IgnoraParametrosQueTentamSelecionarOutroPerfil()
    {
        using var factory = new AlmiranteApiFactory();
        var outro = await TestHelpers.AddUsuarioAsync(factory, "Outro Membro", "DS");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync($"/api/Auth/Me?id={outro.Id}&usuarioId={outro.Id}&email={Uri.EscapeDataString(outro.Email)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal(AlmiranteApiFactory.AdminEmail, body!.Email);
        Assert.NotEqual(outro.Id, body.Id);
    }

    [Fact]
    public async Task Me_RefleteAlteracaoDeNomeEEmailNoBanco()
    {
        using var factory = new AlmiranteApiFactory();
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");

        await TestHelpers.WithDbAsync(factory, async db =>
        {
            var usuario = await db.Usuarios.SingleAsync(u => u.Id == usuarioId);
            usuario.Nome = "Nome Atualizado";
            usuario.Email = "atualizado@local.dev";
            usuario.EmailNormalizado = "ATUALIZADO@LOCAL.DEV";
            return await db.SaveChangesAsync();
        });

        var body = await client.GetFromJsonAsync<MeDto>("/api/Auth/Me");
        Assert.Equal("Nome Atualizado", body!.Nome);
        Assert.Equal("atualizado@local.dev", body.Email);
    }

    [Fact]
    public async Task Me_UsuarioInexistente_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);

        await TestHelpers.WithDbAsync(factory, async db =>
        {
            db.AuthSessions.RemoveRange(db.AuthSessions.Where(s => s.UsuarioId == usuarioId));
            db.Usuarios.Remove(await db.Usuarios.SingleAsync(u => u.Id == usuarioId));
            return await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/Me")).StatusCode);
    }

    [Fact]
    public async Task Usuarios_SemToken_Retorna401()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Usuarios");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Usuarios_ComToken_RetornaListaComAdminSeedado()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/Usuarios");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<UsuarioListItemDto>>();
        Assert.NotNull(body);
        Assert.Contains(body!, u => u.Email == AlmiranteApiFactory.AdminEmail);
    }

    [Fact]
    public async Task Login_ERefresh_ExpoemSomenteEnvelopeToken_EJwtMinimo()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var login = await client.PostAsJsonAsync("/api/Auth/login", new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha });
        login.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal(["token"], document.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Equal(["accessToken", "expiresAtUtc"], document.RootElement.GetProperty("token").EnumerateObject().Select(x => x.Name).ToArray());
        Assert.EndsWith("Z", document.RootElement.GetProperty("token").GetProperty("expiresAtUtc").GetString());
        var raw = document.RootElement.GetProperty("token").GetProperty("accessToken").GetString()!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(raw);
        Assert.Equal("at+jwt", jwt.Header.Typ);
        Assert.Equal("HS256", jwt.Header.Alg);
        Assert.Equal(["aud", "exp", "iat", "iss", "jti", "nbf", "role", "sid", "sub"], jwt.Claims.Select(x => x.Type).Distinct().Order().ToArray());
        Assert.DoesNotContain(jwt.Claims, x => x.Type is "email" or "name" or ClaimTypes.NameIdentifier or "nameid" or "unique_name");
        Assert.DoesNotContain(AlmiranteApiFactory.AdminEmail, Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(raw.Split('.')[1])));

        var refresh = await client.PostAsync("/api/Auth/refresh", null);
        refresh.EnsureSuccessStatusCode();
        using var refreshed = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        Assert.Equal(["token"], refreshed.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Autenticacao_MapeiaSubParaNameIdentifier_ERoleParaIsInRole()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var sub = new JsonWebToken(client.DefaultRequestHeaders.Authorization!.Parameter).Subject;

        using var document = JsonDocument.Parse(await client.GetStringAsync(AlmiranteApiFactory.ClaimsPath));
        var root = document.RootElement;
        var claims = root.GetProperty("claims").EnumerateArray()
            .Select(c => (Type: c.GetProperty("type").GetString()!, Value: c.GetProperty("value").GetString()!)).ToList();

        Assert.True(root.GetProperty("authenticated").GetBoolean());
        Assert.True(root.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(sub, Assert.Single(claims, c => c.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal(sub, Assert.Single(claims, c => c.Type == "sub").Value);
        Assert.Equal(sub, root.GetProperty("name").GetString());
        Assert.DoesNotContain(claims, c => c.Type is ClaimTypes.Email or ClaimTypes.Name or "email" or "name");
    }

    [Fact]
    public async Task Logout_RevogaSessao_EBearerAnteriorDeixaDeAutorizar()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/Auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/Me")).StatusCode);
    }

    // ---- Validação do JWT: cada token forjado difere de um token válido em um único aspecto. ----

    public static TheoryData<string> TokensInvalidos => new()
    {
        "assinatura-adulterada", "emissor-incorreto", "audiencia-incorreta", "algoritmo-hs512",
        "algoritmo-none", "chave-diferente", "expirado-alem-da-tolerancia", "tamanho-excessivo", "typ-incorreto",
    };

    [Theory]
    [MemberData(nameof(TokensInvalidos))]
    public async Task Me_RejeitaTokenInvalido(string cenario)
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        var real = await TestHelpers.LoginAsAdminAsync(client);

        var token = cenario switch
        {
            "assinatura-adulterada" => TamperSignature(Forge(factory, real)),
            "emissor-incorreto" => Forge(factory, real, d => d.Issuer = "outro-emissor"),
            "audiencia-incorreta" => Forge(factory, real, d => d.Audience = "outra-audiencia"),
            "algoritmo-hs512" => Forge(factory, real, algorithm: SecurityAlgorithms.HmacSha512),
            "algoritmo-none" => Unsigned(real),
            "chave-diferente" => Forge(factory, real, key: System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            "expirado-alem-da-tolerancia" => Forge(factory, real, d => { d.IssuedAt = d.NotBefore = DateTime.UtcNow.AddMinutes(-20); d.Expires = DateTime.UtcNow.AddSeconds(-60); }),
            "tamanho-excessivo" => Forge(factory, real, d => d.Claims["pad"] = new string('x', 9000)),
            "typ-incorreto" => Forge(factory, real, d => d.TokenType = "JWT"),
            _ => throw new ArgumentOutOfRangeException(nameof(cenario)),
        };

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(client, token)).StatusCode);
    }

    [Fact]
    public async Task Me_AceitaTokenReassinadoValido_EExpiradoDentroDaTolerancia()
    {
        // Controle dos cenários acima: a forja em si produz tokens aceitos quando nada é alterado.
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        var real = await TestHelpers.LoginAsAdminAsync(client);

        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(client, Forge(factory, real))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(client, Forge(factory, real, d => d.Claims["pad"] = new string('x', 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(client, Forge(factory, real,
            d => { d.IssuedAt = d.NotBefore = DateTime.UtcNow.AddMinutes(-20); d.Expires = DateTime.UtcNow.AddSeconds(-10); }))).StatusCode);
    }

    [Fact]
    public async Task Logs_NaoExpoemSenhaTokenNemAuthorization()
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:Microsoft"] = "Trace",
        });
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        const string senhaErrada = "Senha-Errada-Rastreavel-9f3";

        await TestHelpers.PostLoginAsync(client, AlmiranteApiFactory.AdminEmail, senhaErrada);
        var token = await TestHelpers.LoginAsAdminAsync(client);
        await GetMeAsync(client, token);
        await GetMeAsync(client, TamperSignature(token));

        var logs = string.Join("\n", factory.Logs.Entries);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.DoesNotContain(senhaErrada, logs);
        Assert.DoesNotContain(AlmiranteApiFactory.AdminSenha, logs);
        Assert.DoesNotContain(token.Split('.')[2], logs);
        Assert.DoesNotContain(token.Split('.')[1], logs);
        Assert.DoesNotContain("Bearer ey", logs);
    }

    private static Task<HttpResponseMessage> GetMeAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Auth/Me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static string Forge(AlmiranteApiFactory factory, string realToken, Action<SecurityTokenDescriptor>? mutate = null,
        string algorithm = SecurityAlgorithms.HmacSha256, byte[]? key = null)
    {
        var source = new JsonWebToken(realToken);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = source.Issuer,
            Audience = source.Audiences.Single(),
            IssuedAt = source.IssuedAt,
            NotBefore = source.ValidFrom,
            Expires = source.ValidTo,
            TokenType = "at+jwt",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = source.Subject,
                ["role"] = source.GetClaim("role").Value,
                ["jti"] = Guid.NewGuid().ToString(),
                ["sid"] = source.GetClaim("sid").Value,
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key ?? Convert.FromBase64String(factory.JwtKeyBase64)) { KeyId = factory.JwtKeyId }, algorithm),
        };
        mutate?.Invoke(descriptor);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static string TamperSignature(string token)
    {
        var parts = token.Split('.');
        var signature = parts[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        return $"{parts[0]}.{parts[1]}.{new string(signature)}";
    }

    private static string Unsigned(string token)
    {
        var parts = token.Split('.');
        var header = new JsonWebToken(token);
        var noneHeader = Base64UrlEncoder.Encode($$"""{"alg":"none","kid":"{{header.Kid}}","typ":"at+jwt"}""");
        return $"{noneHeader}.{parts[1]}.";
    }
}
