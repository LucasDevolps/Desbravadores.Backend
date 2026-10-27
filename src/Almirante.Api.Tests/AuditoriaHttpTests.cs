using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Almirante.Api.Tests.AuditoriaHttpTestKit;

namespace Almirante.Api.Tests;

public sealed class AuditoriaHttpApiFactory : AlmiranteApiFactory
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings["Senhas:RecuperacoesPorIp"] = "1";
        settings["AuditoriaHttp:LimiteCapturaBytes"] = "4096";
        // Os testes forçam a gravação (GravadorAuditoriaHttp.GravarPendentesAsync); o ciclo automático fica longe.
        settings["AuditoriaHttp:IntervaloGravacaoMs"] = "60000";
    }

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<IStartupFilter, EndpointLento>();
}

/// <summary>Auditoria HTTP (issue #158) pelo pipeline completo, com banco em memória: cobertura, exclusões e ausência de dados sensíveis.</summary>
public sealed class AuditoriaHttpTests(AuditoriaHttpApiFactory factory) : IClassFixture<AuditoriaHttpApiFactory>
{
    private static string IpUnico() => $"198.51.100.{Random.Shared.Next(1, 254)}";

    private static HttpRequestMessage Requisicao(HttpMethod metodo, string url, string? ip = null, HttpContent? conteudo = null)
    {
        var request = new HttpRequestMessage(metodo, url) { Content = conteudo };
        if (ip is not null) request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, ip);
        return request;
    }

    private async Task<Guid> AdminIdAsync() =>
        await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Email == AlmiranteApiFactory.AdminEmail).Select(u => u.Id).SingleAsync());

    [Fact]
    public async Task GetComSucesso_UmRegistro_ComUsuarioIpv6TraceERespostaSanitizada()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var busca = $"busca-{Guid.NewGuid():N}@exemplo.com";
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var request = Requisicao(HttpMethod.Get, $"/api/Lancamentos?page=1&pageSize=5&search={Uri.EscapeDataString(busca)}", "2001:db8::10");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registro = Assert.Single(await RegistrosAsync(factory, r => r.Url.Contains("pageSize=5&search=")));
        Assert.Equal("GET", registro.MetodoHttp);
        Assert.Equal(200, registro.StatusCode);
        Assert.Equal("https://localhost/api/Lancamentos?page=1&pageSize=5&search=[omitido]", registro.Url);
        Assert.Equal((await AdminIdAsync()).ToString(), registro.IdentificacaoUsuario);
        Assert.Equal("2001:db8::10", registro.IpUsuario);
        Assert.False(string.IsNullOrWhiteSpace(registro.TraceId));
        Assert.Equal(EstadoCapturaHttp.SemCorpo, registro.EstadoCapturaRequest);
        Assert.Equal(SanitizadorAuditoriaHttp.SemCorpo, registro.Request);
        Assert.Equal(EstadoCapturaHttp.Completa, registro.EstadoCapturaResponse);
        Assert.NotNull(JsonDocument.Parse(registro.Response!));
        Assert.Null(registro.Exception);
        Assert.InRange(registro.DuracaoMs, 0, 60_000);
        Assert.InRange(registro.DataRequestUtc, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(TimeSpan.Zero, registro.DataRequestUtc.Offset);
        var cabecalhos = JsonDocument.Parse(registro.Headers).RootElement;
        Assert.Equal(SanitizadorAuditoriaHttp.MarcadorCredencial, cabecalhos.GetProperty("request").GetProperty("Authorization").GetString());
        Assert.StartsWith("application/json", cabecalhos.GetProperty("response").GetProperty("Content-Type").GetString());
        AssertSemSegredos([registro], busca, token, "Bearer ");
    }

    [Fact]
    public async Task CadastroComDadosSensiveis_NenhumValorChegaAAuditoria_EONegocioRecebeTudo()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var cargoId = await TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());
        var email = $"sensivel-{Guid.NewGuid():N}@exemplo.com";
        var cpf = TestHelpers.NovoCpf();
        var cpfResponsavel = TestHelpers.NovoCpf();
        var doencas = $"alergia-rara-{Guid.NewGuid():N}";
        var senha = $"Senha-Forte-{Guid.NewGuid():N}";
        var corpo = new JsonObject
        {
            ["nome"] = "Joaquina Auditada",
            ["email"] = email,
            ["senha"] = senha,
            ["cargoId"] = cargoId.ToString(),
            ["cpf"] = cpf,
            ["rg"] = "MG-99.887.766",
            ["telefone"] = "31998877665",
            ["dataNascimento"] = DateTime.UtcNow.AddYears(-12).ToString("yyyy-MM-dd"),
            ["tipoSanguineo"] = "AB-",
            ["doencasAlergias"] = doencas,
            ["responsavelNome"] = "Responsável Auditado",
            ["responsavelEmail"] = $"resp-{Guid.NewGuid():N}@exemplo.com",
            ["responsavelCpf"] = cpfResponsavel,
            ["responsavelTelefone"] = "31977665544",
            ["endereco"] = new JsonObject { ["cep"] = "04382160", ["numero"] = "Nº QZ", ["complemento"] = "Casa dos fundos" },
        };

        using var response = await client.PostAsJsonAsync("/api/Usuarios", corpo);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var gravado = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Email == email));
        Assert.Equal((cpf, doencas, cpfResponsavel), (gravado.Cpf, gravado.DoencasAlergias, gravado.ResponsavelCpf));
        var registro = await RegistroAsync(factory, "POST", "/api/Usuarios");
        Assert.Equal(EstadoCapturaHttp.Completa, registro.EstadoCapturaRequest);
        Assert.Equal(EstadoCapturaHttp.Completa, registro.EstadoCapturaResponse);
        Assert.Equal((int)response.StatusCode, registro.StatusCode);
        AssertSemSegredos([registro], email, cpf, cpfResponsavel, TestHelpers.MascararCpf(cpf), "99.887.766", "31998877665", "31977665544",
            doencas, "Joaquina", "Responsável Auditado", "04382160", "Nº QZ", "Casa dos fundos", senha, "Fictícia", "Jardim de Teste");
        var request = JsonDocument.Parse(registro.Request).RootElement;
        Assert.Equal(SanitizadorAuditoriaHttp.MarcadorCredencial, request.GetProperty("senha").GetString());
        Assert.Equal(SanitizadorAuditoriaHttp.MarcadorPessoal, request.GetProperty("endereco").GetString());
        Assert.Equal(cargoId.ToString(), request.GetProperty("cargoId").GetString());
    }

    [Fact]
    public async Task Login_NaoGeraRegistro_EmNenhumaVariacaoDeCaixaBarraOuMetodo()
    {
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var marcador = Guid.NewGuid().ToString();

        using var valido = await client.PostAsJsonAsync("/api/Auth/login", new { email = AlmiranteApiFactory.AdminEmail, senha = AlmiranteApiFactory.AdminSenha });
        using var invalido = await client.PostAsJsonAsync("/API/AUTH/LOGIN/", new { email = AlmiranteApiFactory.AdminEmail, senha = "senha-errada-" + marcador });
        using var barras = await client.PostAsJsonAsync("/api//Auth//login", new { email = "x@y.z", senha = "outra-" + marcador });
        using var get = await client.GetAsync("/api/auth/login?senha=" + marcador);
        using var depois = await client.GetAsync("/api/Auth/csrf?ordem=" + marcador.Length);

        Assert.Equal(HttpStatusCode.OK, valido.StatusCode);
        var todos = await RegistrosAsync(factory, r => r.Url.Contains("/api/Auth/csrf?ordem="));
        Assert.Single(todos);
        var registros = await RegistrosAsync(factory, minimo: 0);
        Assert.DoesNotContain(registros, r => PoliticaAuditoriaHttp.Normalizar(Caminho(r)).Contains("/api/auth/login"));
        AssertSemSegredos(registros, AlmiranteApiFactory.AdminSenha, marcador, "senha-errada");
    }

    [Fact]
    public async Task RotasDeCredenciais_SoMetadadosEMarcadores_SemTokensNemSenhas()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var csrf = client.DefaultRequestHeaders.GetValues("X-CSRF-TOKEN").Single();
        var accessToken = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var senhaErrada = $"Senha-Errada-{Guid.NewGuid():N}";
        var novaSenha = $"Nova-Senha-Longa-{Guid.NewGuid():N}";

        using var alterar = await client.PostAsJsonAsync("/api/Auth/alterar-senha",
            new { senhaAtualSolicitante = senhaErrada, novaSenha, confirmacaoNovaSenha = novaSenha + "-diferente" });
        using var me = await client.GetAsync("/api/Auth/Me");
        using var refresh = await client.PostAsync("/api/Auth/refresh", null);

        Assert.Equal(HttpStatusCode.BadRequest, alterar.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var registroAlterar = await RegistroAsync(factory, "POST", "/api/Auth/alterar-senha");
        Assert.Equal(EstadoCapturaHttp.OmitidaPorSeguranca, registroAlterar.EstadoCapturaRequest);
        Assert.Equal(EstadoCapturaHttp.OmitidaPorSeguranca, registroAlterar.EstadoCapturaResponse);
        Assert.Equal(400, registroAlterar.StatusCode);
        Assert.Contains(ConteudoSensivelAttribute.MarcadorOmitido, JsonDocument.Parse(registroAlterar.Request).RootElement.GetProperty("omitido").GetString());
        var registroMe = await RegistroAsync(factory, "GET", "/api/Auth/Me");
        Assert.Equal(EstadoCapturaHttp.OmitidaPorSeguranca, registroMe.EstadoCapturaResponse);
        var csrfRegistros = await RegistrosAsync(factory, r => Caminho(r) == "/api/Auth/csrf");
        Assert.All(csrfRegistros, r => Assert.Equal(EstadoCapturaHttp.OmitidaPorSeguranca, r.EstadoCapturaResponse));
        var registros = await RegistrosAsync(factory, minimo: 0);
        AssertSemSegredos(registros, csrf, accessToken, senhaErrada, novaSenha, AlmiranteApiFactory.AdminEmail, "CfDJ8");
        Assert.Contains(registros, r => r.Headers.Contains("\"Set-Cookie\":\"[credencial omitida]\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Erros_404_401_403_400_SaoRegistrados_ComStatusERespostaFinal()
    {
        var cpf = TestHelpers.NovoCpf();
        var email = $"nf-{Guid.NewGuid():N}@exemplo.com";
        using var anonimo = factory.CreateClient();
        using var naoEncontrado = await anonimo.GetAsync($"/api/NaoExiste/{cpf}?email={Uri.EscapeDataString(email)}");
        using var naoAutenticado = await anonimo.GetAsync("/api/Usuarios?ordem=401");
        var (desbravador, desbravadorId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        using var proibido = await desbravador.GetAsync("/api/Lancamentos?ordem=403");
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        using var invalido = await admin.PutAsJsonAsync($"/api/Lancamentos/{Guid.NewGuid()}?ordem=400", new { valor = -1, descricao = $"contato {email}" });

        Assert.Equal(HttpStatusCode.NotFound, naoEncontrado.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, naoAutenticado.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, proibido.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        var registro404 = Assert.Single(await RegistrosAsync(factory, r => r.Url.Contains("/api/[omitido]/[omitido]")));
        Assert.Equal((404, AuditoriaHttp.Anonimo), (registro404.StatusCode, registro404.IdentificacaoUsuario));
        Assert.Equal("https://localhost/api/[omitido]/[omitido]?email=[omitido]", registro404.Url);
        var registro401 = Assert.Single(await RegistrosAsync(factory, r => r.Url.EndsWith("/api/Usuarios?ordem=401")));
        Assert.Equal((401, AuditoriaHttp.Anonimo), (registro401.StatusCode, registro401.IdentificacaoUsuario));
        var registro403 = Assert.Single(await RegistrosAsync(factory, r => r.Url.EndsWith("/api/Lancamentos?ordem=403")));
        Assert.Equal(403, registro403.StatusCode);
        Assert.Equal(desbravadorId.ToString(), registro403.IdentificacaoUsuario);
        Assert.Equal(EstadoCapturaHttp.Completa, registro403.EstadoCapturaResponse);
        Assert.Equal("ACESSO NEGADO!", JsonDocument.Parse(registro403.Response!).RootElement.GetProperty("title").GetString());
        var registro400 = Assert.Single(await RegistrosAsync(factory, r => r.MetodoHttp == "PUT" && r.Url.EndsWith("?ordem=400")));
        Assert.Equal(400, registro400.StatusCode);
        Assert.Equal(EstadoCapturaHttp.Completa, registro400.EstadoCapturaRequest);
        Assert.True(JsonDocument.Parse(registro400.Response!).RootElement.TryGetProperty("errors", out _));
        AssertSemSegredos([registro404, registro401, registro403, registro400], cpf, email);
    }

    [Fact]
    public async Task MuitasTentativas_429_ProduzidoNaApi_EhRegistrado()
    {
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var ip = IpUnico();
        var email = $"rec-{Guid.NewGuid():N}@exemplo.com";

        using var primeiro = await client.SendAsync(Requisicao(HttpMethod.Post, "/api/Auth/recuperacao-senha/solicitar", ip, JsonContent.Create(new { email })));
        using var segundo = await client.SendAsync(Requisicao(HttpMethod.Post, "/api/Auth/recuperacao-senha/solicitar", ip, JsonContent.Create(new { email })));

        Assert.Equal(HttpStatusCode.TooManyRequests, segundo.StatusCode);
        var registros = await RegistrosAsync(factory, r => r.IpUsuario == ip && Caminho(r) == "/api/Auth/recuperacao-senha/solicitar", minimo: 2);
        Assert.Equal([202, 429], registros.Select(r => r.StatusCode ?? 0).Order());
        var registro429 = registros.Single(r => r.StatusCode == 429);
        Assert.Contains("Retry-After", registro429.Headers);
        AssertSemSegredos(registros, email);
    }

    [Fact]
    public async Task ExcecaoTratada_RegistraDiagnosticoSeguroERespostaFinal_UmaVezSo()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(AlmiranteApiFactory.ThrowPath + "?ordem=500");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var registro = Assert.Single(await RegistrosAsync(factory, r => r.Url.EndsWith("?ordem=500")));
        Assert.Equal("https://localhost/[omitido]/[omitido]?ordem=500", registro.Url);
        Assert.Equal(500, registro.StatusCode);
        var excecao = JsonDocument.Parse(registro.Exception!).RootElement;
        Assert.Equal(typeof(InvalidOperationException).FullName, excecao.GetProperty("tipo").GetString());
        Assert.True(excecao.GetProperty("tratadaPelaApi").GetBoolean());
        Assert.Equal(500, JsonDocument.Parse(registro.Response!).RootElement.GetProperty("status").GetInt32());
        AssertSemSegredos([registro], "falha simulada");
    }

    [Fact]
    public async Task SemConteudo_204_ResponseNula_ERequestComMarcador()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        using var logout = await client.PostAsync("/api/Auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var registro = await RegistroAsync(factory, "POST", "/api/Auth/logout");
        Assert.Equal(204, registro.StatusCode);
        Assert.Null(registro.Response);
        Assert.Equal(EstadoCapturaHttp.SemCorpo, registro.EstadoCapturaResponse);
        Assert.Equal(SanitizadorAuditoriaHttp.SemCorpo, registro.Request);
        Assert.Null(registro.Exception);
    }

    [Fact]
    public async Task UrlAcimaDoLimiteDaColuna_ETruncadaComReticencias()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var marca = $"longa{Guid.NewGuid():N}";
        var query = string.Join("&", Enumerable.Range(0, 300).Select(i => $"{marca}{i}=1"));

        using var response = await client.GetAsync($"/api/Lancamentos?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registro = Assert.Single(await RegistrosAsync(factory, r => r.Url.Contains(marca)));
        Assert.Equal(AuditoriaHttp.UrlMaximo, registro.Url.Length);
        Assert.EndsWith("...", registro.Url);
    }

    [Fact]
    public async Task CorpoMaiorQueOLimite_ApiRecebeTudo_RegistroSoComMetadados()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var segredo = $"cpf-{TestHelpers.NovoCpf()}";
        var corpo = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { descricao = segredo + new string('x', 8_000), valor = 10 }));
        var comTamanho = new ByteArrayContent(corpo) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        var semTamanho = new StreamContent(new NaoBuscavel(corpo)) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        var rota = $"/api/Lancamentos/{Guid.NewGuid()}";
        using var requestChunked = new HttpRequestMessage(HttpMethod.Put, rota + "?tamanho=chunked") { Content = semTamanho };
        requestChunked.Headers.TransferEncodingChunked = true;

        using var declarado = await client.PutAsync(rota + "?tamanho=declarado", comTamanho);
        using var chunked = await client.SendAsync(requestChunked);

        Assert.Equal(HttpStatusCode.BadRequest, declarado.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, chunked.StatusCode);
        Assert.Equal(await declarado.Content.ReadAsStringAsync().ContinueWith(t => JsonNode.Parse(t.Result)!["errors"]!.ToJsonString()),
            await chunked.Content.ReadAsStringAsync().ContinueWith(t => JsonNode.Parse(t.Result)!["errors"]!.ToJsonString()));
        var registros = await RegistrosAsync(factory, r => r.Url.Contains("tamanho="), minimo: 2);
        Assert.All(registros, r => Assert.Equal(EstadoCapturaHttp.OmitidaPorTamanho, r.EstadoCapturaRequest));
        Assert.All(registros, r => Assert.Equal(400, r.StatusCode));
        AssertSemSegredos(registros, segredo, "xxxxxxxx");
    }

    [Fact]
    public async Task UploadEDownloadDeFoto_SoTipoETamanho_ArquivoNuncaGravado()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var alvo = await TestHelpers.AddUsuarioAsync(factory, "Foto Auditada", "DS");
        var chave = FotoChave.Gerar(alvo.Id);
        await factory.Services.GetRequiredService<IFileStorage>().SaveAsync(chave, FotosTestKit.Webp(120, 120), CancellationToken.None);
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            (await db.Usuarios.SingleAsync(u => u.Id == alvo.Id)).FotoCaminho = chave;
            return await db.SaveChangesAsync();
        });

        // Em memória o envio termina em 500 (a gravação da foto exige SQL Server): o registro guarda a exceção sem a mensagem.
        using var upload = await client.PutAsync($"/api/Me/foto?ordem=1", FotosTestKit.Multipart(FotosTestKit.Jpeg(120, 120)));
        using var download = await client.GetAsync($"/api/Usuarios/{alvo.Id}/foto");
        var bytes = await download.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var registroUpload = Assert.Single(await RegistrosAsync(factory, r => r.Url.EndsWith("/api/Me/foto?ordem=1")));
        Assert.Equal((int)upload.StatusCode, registroUpload.StatusCode);
        Assert.Equal(EstadoCapturaHttp.OmitidaPorTipo, registroUpload.EstadoCapturaRequest);
        Assert.Equal("multipart/form-data", JsonDocument.Parse(registroUpload.Request).RootElement.GetProperty("tipoConteudo").GetString());
        AssertSemSegredos([registroUpload], "exigem SQL Server");
        var registroDownload = await RegistroAsync(factory, "GET", $"/api/Usuarios/{alvo.Id}/foto");
        Assert.Equal(EstadoCapturaHttp.OmitidaPorTipo, registroDownload.EstadoCapturaResponse);
        var marcador = JsonDocument.Parse(registroDownload.Response!).RootElement;
        Assert.Equal(("image/webp", bytes.Length), (marcador.GetProperty("tipoConteudo").GetString(), marcador.GetProperty("tamanhoBytes").GetInt32()));
        AssertSemSegredos([registroUpload, registroDownload], Convert.ToBase64String(bytes)[..40], "RIFF", "JFIF");
    }

    [Fact]
    public async Task Hub_RegistraNegociacaoEHandshake_SemTokenNaUrl()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;

        using var negociacao = await client.PostAsync("/hubs/notificacoes/negotiate?negotiateVersion=1", null);
        using var anonimo = factory.CreateClient();
        using var handshake = await anonimo.GetAsync($"/hubs/notificacoes?id=conexao-invalida&access_token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, negociacao.StatusCode);
        var registros = await RegistrosAsync(factory, r => Caminho(r).StartsWith("/hubs/notificacoes", StringComparison.Ordinal), minimo: 2);
        Assert.Contains(registros, r => r.Url.EndsWith("/hubs/notificacoes/negotiate?negotiateVersion=1"));
        Assert.Contains(registros, r => r.Url.EndsWith("/hubs/notificacoes?id=[omitido]&access_token=[omitido]"));
        var corpoNegociacao = await negociacao.Content.ReadFromJsonAsync<JsonElement>();
        AssertSemSegredos(registros, token, corpoNegociacao.GetProperty("connectionToken").GetString()!, corpoNegociacao.GetProperty("connectionId").GetString()!);
    }

    [Fact]
    public async Task WebSocket_RegistradoNoUpgrade101_SemEsperarAConexaoFechar_ESemToken()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var ws = factory.Server.CreateWebSocketClient();

        using var socket = await ws.ConnectAsync(new Uri($"wss://localhost/hubs/notificacoes?ordem=101&access_token={Uri.EscapeDataString(token)}"), CancellationToken.None);

        var registro = Assert.Single(await RegistrosAsync(factory, r => r.Url.Contains("ordem=101")));
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State);
        Assert.Equal(101, registro.StatusCode);
        Assert.Equal(EstadoCapturaHttp.OmitidaPorTipo, registro.EstadoCapturaResponse);
        Assert.Contains("WebSocket", JsonDocument.Parse(registro.Response!).RootElement.GetProperty("omitido").GetString());
        Assert.Equal((await AdminIdAsync()).ToString(), registro.IdentificacaoUsuario);
        Assert.EndsWith("/hubs/notificacoes?ordem=101&access_token=[omitido]", registro.Url);
        AssertSemSegredos([registro], token);
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task ClienteDesiste_RegistraInterrompida_SemStatusPadraoInventado()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(AuditoriaHttpTestKit.LentoPath + "?ordem=7", HttpCompletionOption.ResponseHeadersRead);
        response.Dispose();

        var registro = Assert.Single(await RegistrosAsync(factory, r => r.Url.EndsWith("?ordem=7"), prazoMs: 20_000));
        Assert.Equal(EstadoCapturaHttp.Interrompida, registro.EstadoCapturaResponse);
        Assert.Equal(200, registro.StatusCode);
    }

    [Fact]
    public async Task ClienteDesisteAntesDaResposta_StatusNulo_ResponseNula()
    {
        using var client = factory.CreateClient();
        using var prazo = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(AuditoriaHttpTestKit.LentoAntesDeResponderPath + "?ordem=8", prazo.Token));

        var registro = Assert.Single(await RegistrosAsync(factory, r => r.Url.EndsWith("?ordem=8"), prazoMs: 20_000));
        Assert.Null(registro.StatusCode);
        Assert.Null(registro.Response);
        Assert.Equal(EstadoCapturaHttp.Interrompida, registro.EstadoCapturaResponse);
        Assert.Equal(SanitizadorAuditoriaHttp.SemCorpo, registro.Request);
    }

    [Fact]
    public async Task ApiDesabilitada_NaoRegistraNada()
    {
        using var desligada = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["AuditoriaHttp:Habilitada"] = "false" });
        using var client = desligada.CreateClient();

        using var response = await client.GetAsync("/api/Auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await RegistrosAsync(desligada, minimo: 0, prazoMs: 500));
    }

    /// <summary>Stream sem tamanho conhecido: o HttpClient envia chunked, sem Content-Length.</summary>
    private sealed class NaoBuscavel(byte[] conteudo) : MemoryStream(conteudo)
    {
        public override bool CanSeek => false;
    }
}
