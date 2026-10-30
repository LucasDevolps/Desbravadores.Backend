using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Almirante.Api.Tests;

/// <summary>
/// Garantias de resiliência da auditoria HTTP (issue #158): uma falha dentro da auditoria nunca derruba nem altera a requisição, não vaza
/// conteúdo e deixa rastro no log (só o tipo da exceção). As falhas são injetadas apenas quando a própria auditoria está na pilha.
/// </summary>
public sealed class AuditoriaHttpResilienciaTests
{
    private static bool DentroDaAuditoria(string? metodo = null) => new StackTrace().GetFrames().Any(f =>
        f.GetMethod() is { DeclaringType.Name: "ChamadaAuditada" } m && (metodo is null || m.Name == metodo));

    /// <summary>
    /// IHttpRequestFeature que repassa tudo, mas falha ao entregar <c>Body</c> ao construtor da chamada auditada ou <c>Path</c> à montagem
    /// da URL (o resto da API lê os mesmos valores normalmente).
    /// </summary>
    private sealed class RequisicaoQueFalhaNaAuditoria(IHttpRequestFeature original, string membro) : IHttpRequestFeature
    {
        private T Talvez<T>(string nome, T valor) =>
            nome == membro && DentroDaAuditoria(nome == nameof(Path) ? "MontarUrl" : null) ? throw new InvalidOperationException($"falha simulada em {nome}") : valor;

        public string Protocol { get => original.Protocol; set => original.Protocol = value; }
        public string Scheme { get => original.Scheme; set => original.Scheme = value; }
        public string Method { get => original.Method; set => original.Method = value; }
        public string PathBase { get => original.PathBase; set => original.PathBase = value; }
        public string Path { get => Talvez(nameof(Path), original.Path); set => original.Path = value; }
        public string QueryString { get => original.QueryString; set => original.QueryString = value; }
        public string RawTarget { get => original.RawTarget; set => original.RawTarget = value; }
        public IHeaderDictionary Headers { get => original.Headers; set => original.Headers = value; }
        public Stream Body { get => Talvez(nameof(Body), original.Body); set => original.Body = value; }
    }

    /// <summary>Usuário cuja identidade não pode ser lida pela montagem do registro (a versão mínima, sem conteúdo, ainda funciona).</summary>
    private sealed class UsuarioQueFalhaAoMontarORegistro : ClaimsPrincipal
    {
        public override System.Security.Principal.IIdentity? Identity =>
            DentroDaAuditoria("Montar") ? throw new InvalidOperationException("falha simulada na identidade") : new ClaimsIdentity();
    }

    private const string Prefixo = "/__tests/falha";

    private sealed class EndpointsComFalha(string arquivo) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, proximo) =>
            {
                if (context.Request.Path.StartsWithSegments(Prefixo) && context.Request.Query["falha"] is { Count: 1 } falha)
                {
                    if (falha == "usuario") context.User = new UsuarioQueFalhaAoMontarORegistro();
                    else context.Features.Set<IHttpRequestFeature>(new RequisicaoQueFalhaNaAuditoria(context.Features.GetRequiredFeature<IHttpRequestFeature>(), falha!));
                }
                return proximo(context);
            });

            next(app);

            app.Use(async (context, proximo) =>
            {
                switch (context.Request.Path.Value)
                {
                    case Prefixo + "/eco":
                        using (var leitor = new StreamReader(context.Request.Body))
                        {
                            var corpo = await leitor.ReadToEndAsync();
                            await context.Response.WriteAsJsonAsync(new { recebido = corpo.Length });
                        }
                        return;
                    case Prefixo + "/parcial":
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("""{"parcial":""");
                        await context.Response.Body.FlushAsync();
                        throw new InvalidOperationException("falha depois de começar a resposta");
                    case Prefixo + "/cabecalhos":
                        context.Response.ContentType = "application/json";
                        await context.Response.StartAsync();
                        throw new InvalidOperationException("falha depois dos cabeçalhos, sem corpo");
                    case Prefixo + "/lanca":
                        throw new InvalidOperationException("falha antes da resposta");
                    case Prefixo + "/pipe":
                        // Content-Length menor que o que fica no buffer do PipeWriter: o servidor recusa no flush final.
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength = 2;
                        var bytes = """{"a":1}"""u8;
                        bytes.CopyTo(context.Response.BodyWriter.GetSpan(bytes.Length));
                        context.Response.BodyWriter.Advance(bytes.Length);
                        return;
                    case Prefixo + "/arquivo":
                        context.Response.ContentType = "application/json";
                        if (context.Request.Query.ContainsKey("trecho")) await context.Response.SendFileAsync(arquivo, 0, 7);
                        else await context.Response.SendFileAsync(arquivo);
                        return;
                }
                await proximo(context);
            });
        };
    }

    private sealed class FactoryComFalhas : AlmiranteApiFactory
    {
        public string Arquivo { get; } = Path.Combine(Path.GetTempPath(), $"almirante-teste-arquivo-{Guid.NewGuid():N}.json");

        public FactoryComFalhas() => File.WriteAllText(Arquivo, """{"conteudo":"do arquivo"}""");

        protected override void ConfigureTestServices(IServiceCollection services) =>
            services.AddSingleton<IStartupFilter>(new EndpointsComFalha(Arquivo));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) File.Delete(Arquivo);
        }
    }

    // Os segmentos /__tests/falha/... não são literais da API e saem como marcador na URL: cada teste usa uma factory nova e faz uma
    // única chamada auditada, então o registro dela é o único com o método usado.
    private static Task<List<AuditoriaHttp>> RegistrosDoCaminhoAsync(AlmiranteApiFactory factory, string metodo, int minimo = 1) =>
        AuditoriaHttpTestKit.RegistrosAsync(factory, r => r.MetodoHttp == metodo, minimo);

    [Fact]
    public async Task FalhaAoAcompanharAChamada_ARequisicaoSegueIntacta_SemRegistro_ComLogSoDoTipo()
    {
        await using var factory = new FactoryComFalhas();
        var resposta = await factory.CreateClient().PostAsJsonAsync($"{Prefixo}/eco?falha=Body", new { segredo = "nao-deve-aparecer" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("""{"segredo":"nao-deve-aparecer"}""".Length,
            (await resposta.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("recebido").GetInt32());
        Assert.Contains(factory.Logs.Entries, e => e.Contains("Auditoria HTTP não conseguiu acompanhar uma chamada (System.InvalidOperationException)"));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Contains("nao-deve-aparecer"));
        Assert.Empty(await RegistrosDoCaminhoAsync(factory, "POST", minimo: 0));
    }

    [Fact]
    public async Task UrlQueNaoPodeSerLida_VaiComoMarcador()
    {
        await using var factory = new FactoryComFalhas();
        var resposta = await factory.CreateClient().PostAsJsonAsync($"{Prefixo}/eco?falha=Path", new { a = 1 });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var registro = Assert.Single(await AuditoriaHttpTestKit.RegistrosAsync(factory, r => r.Url.EndsWith(SanitizadorAuditoriaHttp.MarcadorUrl)));
        Assert.Equal($"https://{SanitizadorAuditoriaHttp.MarcadorUrl}", registro.Url);
        Assert.Equal(200, registro.StatusCode);
    }

    [Fact]
    public async Task FalhaAoMontarORegistro_GravaSoOMinimo_SemConteudo()
    {
        await using var factory = new FactoryComFalhas();
        var resposta = await factory.CreateClient().PostAsJsonAsync($"{Prefixo}/eco?falha=usuario", new { segredo = "nao-deve-aparecer" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var registro = Assert.Single(await RegistrosDoCaminhoAsync(factory, "POST"));
        Assert.Equal((EstadoCapturaHttp.FalhaSanitizacao, EstadoCapturaHttp.FalhaSanitizacao), (registro.EstadoCapturaRequest, registro.EstadoCapturaResponse));
        Assert.Equal("{}", registro.Headers);
        Assert.Equal(registro.Request, registro.Response);
        Assert.Contains("falha na sanitização", registro.Request);
        Assert.Equal((200, AuditoriaHttp.Anonimo, "POST"), (registro.StatusCode, registro.IdentificacaoUsuario, registro.MetodoHttp));
        Assert.DoesNotContain("nao-deve-aparecer", AuditoriaHttpTestKit.Tudo(registro));
        Assert.Contains(factory.Logs.Entries, e => e.Contains($"Falha ao sanitizar a auditoria HTTP {registro.Id} (System.InvalidOperationException)"));
    }

    /// <summary>Kestrel de verdade (em processo): Content-Length, desconexão e OnCompleted com exceção só se comportam assim nele.</summary>
    private static void Kestrel(FactoryComFalhas factory)
    {
        factory.UseKestrel(0);
        factory.StartServer();
    }

    [Theory]
    [InlineData("/parcial")]
    [InlineData("/lanca")]
    public async Task FalhaAoMontarORegistro_ComExcecaoNaoTratada_GravaOMinimo(string rota)
    {
        await using var factory = new FactoryComFalhas();
        Kestrel(factory);
        try
        {
            using var resposta = await factory.CreateClient().GetAsync($"{Prefixo}{rota}?falha=usuario");
            await resposta.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException)
        {
        }

        var registro = Assert.Single(await RegistrosDoCaminhoAsync(factory, "GET"));
        Assert.Equal((EstadoCapturaHttp.FalhaSanitizacao, EstadoCapturaHttp.FalhaSanitizacao), (registro.EstadoCapturaRequest, registro.EstadoCapturaResponse));
        Assert.Contains("falha na sanitização", registro.Request);
    }

    [Fact]
    public async Task FlushFinalRecusadoPeloServidor_NaoSubstituiAChamada_ERegistroSai()
    {
        await using var factory = new FactoryComFalhas();
        Kestrel(factory);
        try
        {
            using var resposta = await factory.CreateClient().GetAsync($"{Prefixo}/pipe");
            await resposta.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException)
        {
        }

        Assert.Single(await RegistrosDoCaminhoAsync(factory, "GET"));
    }

    private static async Task<System.Net.Sockets.TcpClient> ConectarAsync(FactoryComFalhas factory)
    {
        var endereco = new Uri(factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
        var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(endereco.Host == "localhost" ? "127.0.0.1" : endereco.Host, endereco.Port);
        return tcp;
    }

    [Fact]
    public async Task ExcecaoDepoisDosCabecalhosSemCorpo_RespostaInterrompidaComMarcador()
    {
        await using var factory = new FactoryComFalhas();
        Kestrel(factory);
        try
        {
            using var resposta = await factory.CreateClient().GetAsync($"{Prefixo}/cabecalhos");
            await resposta.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException)
        {
        }

        var registro = Assert.Single(await RegistrosDoCaminhoAsync(factory, "GET"));
        Assert.Equal((EstadoCapturaHttp.Interrompida, 200), (registro.EstadoCapturaResponse, registro.StatusCode));
        Assert.Contains("resposta interrompida", registro.Response);
    }

    [Fact]
    public async Task ClienteFechaAConexaoNoMeioDoCorpo_RequisicaoInterrompida()
    {
        await using var factory = new FactoryComFalhas();
        Kestrel(factory);

        using (var tcp = await ConectarAsync(factory))
        {
            // Cabeçalhos com Content-Length 1000 e só o começo do corpo; depois a conexão fecha.
            var parcial = System.Text.Encoding.ASCII.GetBytes(string.Join("\r\n",
                $"POST {Prefixo}/eco HTTP/1.1", "Host: localhost", "Content-Type: application/json", "Content-Length: 1000", "", "{\"a\":\""));
            await tcp.GetStream().WriteAsync(parcial);
            await tcp.GetStream().FlushAsync();
            await Task.Delay(300);
        }

        var registro = Assert.Single(await RegistrosDoCaminhoAsync(factory, "POST"));
        Assert.Equal(EstadoCapturaHttp.Interrompida, registro.EstadoCapturaRequest);
        Assert.Contains("requisição interrompida", registro.Request);
    }

    [Fact]
    public async Task ExcecaoDepoisDeComecarAResposta_EhRegistradaComoNaoTratada_ERespostaInterrompida()
    {
        // Kestrel de verdade (em processo): o TestServer não dispara OnCompleted quando a aplicação lança, o servidor real dispara.
        await using var factory = new FactoryComFalhas();
        factory.UseKestrel(0);
        factory.StartServer();
        try
        {
            using var resposta = await factory.CreateClient().GetAsync($"{Prefixo}/parcial");
            await resposta.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException)
        {
        }

        var registro = Assert.Single(await RegistrosDoCaminhoAsync(factory, "GET"));
        Assert.Equal(EstadoCapturaHttp.Interrompida, registro.EstadoCapturaResponse);
        Assert.Equal(200, registro.StatusCode);
        Assert.Contains("\"tratadaPelaApi\":false", registro.Exception);
        Assert.Contains("System.InvalidOperationException", registro.Exception);
        Assert.DoesNotContain("falha depois de começar a resposta", registro.Exception);
    }

    [Theory]
    [InlineData("", 25)]
    [InlineData("&trecho=1", 7)]
    public async Task ArquivoEnviadoDiretamente_NaoTemOConteudoCapturado(string query, int bytes)
    {
        await using var factory = new FactoryComFalhas();
        var resposta = await factory.CreateClient().GetAsync($"{Prefixo}/arquivo?x=1{query}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(bytes, (await resposta.Content.ReadAsByteArrayAsync()).Length);
        var registro = Assert.Single(await RegistrosDoCaminhoAsync(factory, "GET"));
        Assert.Equal(EstadoCapturaHttp.OmitidaPorTipo, registro.EstadoCapturaResponse);
        Assert.Contains("arquivo enviado diretamente", registro.Response);
        Assert.Contains($"\"tamanhoBytes\":{bytes}", registro.Response);
        Assert.DoesNotContain("do arquivo", registro.Response);
    }

    [Fact]
    public async Task MetodoForaDoPadrao_IpV4MapeadoEmV6_SaoNormalizados()
    {
        await using var factory = new FactoryComFalhas();
        using var requisicao = new HttpRequestMessage(new HttpMethod("METODO_COM_SUBLINHADO"), $"{Prefixo}/inexistente");
        requisicao.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, "::ffff:203.0.113.9");
        await factory.CreateClient().SendAsync(requisicao);

        var registro = Assert.Single(await AuditoriaHttpTestKit.RegistrosAsync(factory, r => r.MetodoHttp == "OUTRO"));
        Assert.Equal("203.0.113.9", registro.IpUsuario);
    }

    /// <summary>Relógio que passa a falhar (exceção que não é de E/S) quando o teste manda: o diário não consegue registrar.</summary>
    private sealed class RelogioQueFalha : TimeProvider
    {
        public bool Falhar { get; set; }
        public override DateTimeOffset GetUtcNow() => Falhar ? throw new InvalidOperationException("relógio indisponível (simulado)") : base.GetUtcNow();
    }

    private sealed class FactoryComDiarioQueFalha(RelogioQueFalha relogio) : AlmiranteApiFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services) =>
            services.AddSingleton(sp => new DiarioAuditoriaHttp(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditoriaHttpOptions>>(),
                sp.GetRequiredService<IConfiguration>(), relogio, sp.GetRequiredService<ILogger<DiarioAuditoriaHttp>>()));
    }

    [Fact]
    public async Task DiarioQueNaoRegistra_ARespostaNaoMuda_EAFalhaFicaNoLog()
    {
        var relogio = new RelogioQueFalha();
        await using var factory = new FactoryComDiarioQueFalha(relogio);
        var client = factory.CreateClient();
        await factory.Services.GetRequiredService<GravadorAuditoriaHttp>().GravarPendentesAsync(CancellationToken.None);
        relogio.Falhar = true;

        var resposta = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var fim = DateTime.UtcNow.AddSeconds(10);
        while (!factory.Logs.Entries.Any(e => e.Contains("Falha ao registrar a auditoria HTTP")) && DateTime.UtcNow < fim) await Task.Delay(20);
        Assert.Contains(factory.Logs.Entries, e => e.Contains("Falha ao registrar a auditoria HTTP") && e.Contains("(System.InvalidOperationException)"));
        relogio.Falhar = false;
    }

    // --- Gravador e health check, sem host ---

    private sealed class RepositorioRoteirizado(Func<int, IReadOnlyList<AuditoriaHttp>, CancellationToken, Task> roteiro) : IRepositorioAuditoriaHttp
    {
        private int _chamadas;
        public int Chamadas => _chamadas;
        public Task InserirAsync(IReadOnlyList<AuditoriaHttp> registros, CancellationToken cancellationToken) =>
            roteiro(Interlocked.Increment(ref _chamadas), registros, cancellationToken);
    }

    private static AuditoriaHttp Registro() => new()
    {
        Id = Guid.CreateVersion7(), Url = "https://localhost/api/Cargos", MetodoHttp = "GET", Request = SanitizadorAuditoriaHttp.SemCorpo, Headers = "{}",
        DataRequestUtc = DateTimeOffset.UtcNow, IpUsuario = "127.0.0.1", IdentificacaoUsuario = AuditoriaHttp.Anonimo, TraceId = "t", StatusCode = 200,
        EstadoCapturaRequest = EstadoCapturaHttp.SemCorpo, EstadoCapturaResponse = EstadoCapturaHttp.SemCorpo,
    };

    private static (DiarioAuditoriaHttp Diario, string Pasta) DiarioTemporario(AuditoriaHttpOptions opcoes)
    {
        var pasta = Path.Combine(Path.GetTempPath(), "almirante-testes-diario", Guid.NewGuid().ToString("N"));
        opcoes.DiretorioPendentes = pasta;
        return (new DiarioAuditoriaHttp(Microsoft.Extensions.Options.Options.Create(opcoes), new ConfigurationBuilder().Build(), TimeProvider.System,
            NullLogger<DiarioAuditoriaHttp>.Instance), pasta);
    }

    [Fact]
    public async Task Gravador_ParadoNoMeioDeUmaGravacao_EncerraSemErro_EAUltimaGravacaoLevaOsPendentes()
    {
        var opcoes = new AuditoriaHttpOptions { TamanhoLote = 10 };
        var (diario, pasta) = DiarioTemporario(opcoes);
        var chegou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gravados = new List<Guid>();
        var repositorio = new RepositorioRoteirizado(async (chamada, registros, token) =>
        {
            if (chamada == 1)
            {
                chegou.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            gravados.AddRange(registros.Select(r => r.Id));
        });
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        try
        {
            using var gravador = new GravadorAuditoriaHttp(diario, repositorio, Microsoft.Extensions.Options.Options.Create(opcoes), relogio,
                logs.CreateLogger<GravadorAuditoriaHttp>());
            var registro = Registro();
            await diario.RegistrarAsync(registro);
            await gravador.StartAsync(CancellationToken.None);
            await relogio.AguardarTimerAsync();
            relogio.Disparar();
            await chegou.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await gravador.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(gravador.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal([registro.Id], gravados);
            Assert.Empty(logs.Entries);
        }
        finally
        {
            diario.Dispose();
            Directory.Delete(pasta, recursive: true);
        }
    }

    [Fact]
    public async Task Gravador_LoteRecusado_ERetentativaIndividualComBancoFora_NaoApagaNada_ESinalizaNoHealthDepoisDeUmMinuto()
    {
        var opcoes = new AuditoriaHttpOptions { TamanhoLote = 10 };
        var (diario, pasta) = DiarioTemporario(opcoes);
        var repositorio = new RepositorioRoteirizado((chamada, _, _) => chamada == 1
            ? Task.FromException(new InvalidOperationException("lote recusado", SqlExceptionDeTeste.Criar(2628)))
            : Task.FromException(new TimeoutException("banco caiu no meio da retentativa")));
        var relogio = new RelogioManualDeTeste();
        var logs = new CapturingLoggerFactory();
        try
        {
            var gravador = new GravadorAuditoriaHttp(diario, repositorio, Microsoft.Extensions.Options.Options.Create(opcoes), relogio,
                logs.CreateLogger<GravadorAuditoriaHttp>());
            await diario.RegistrarAsync(Registro());
            await diario.RegistrarAsync(Registro());

            Assert.False(await gravador.GravarPendentesAsync(CancellationToken.None));

            Assert.Equal(2, repositorio.Chamadas);
            Assert.Equal(relogio.Agora, gravador.FalhandoDesde);
            Assert.Single(Directory.GetFiles(pasta, "*" + DiarioAuditoriaHttp.ExtensaoPendente));
            Assert.False(Directory.Exists(Path.Combine(pasta, DiarioAuditoriaHttp.PastaRejeitados)));
            Assert.Contains(logs.Entries, e => e.StartsWith("Warning Auditoria HTTP não conseguiu gravar no banco (TimeoutException", StringComparison.Ordinal));

            var check = new AuditoriaHttpHealthCheck(diario, gravador, relogio);
            var contexto = new HealthCheckContext { Registration = new HealthCheckRegistration("auditoria-http", check, HealthStatus.Degraded, null) };
            relogio.Agora += AuditoriaHttpHealthCheck.ToleranciaBanco;
            Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(contexto)).Status);
            relogio.Agora += TimeSpan.FromSeconds(1);
            var saude = await check.CheckHealthAsync(contexto);
            Assert.Equal(HealthStatus.Degraded, saude.Status);
            Assert.Contains($"banco recusando a gravação desde {gravador.FalhandoDesde:O}", saude.Description);
        }
        finally
        {
            diario.Dispose();
            Directory.Delete(pasta, recursive: true);
        }
    }

    [Fact]
    public async Task Gravador_FalhaQueNaoEhCancelamentoDuranteODesligamento_ERegistrada_ESaiDoLaco()
    {
        var opcoes = new AuditoriaHttpOptions { TamanhoLote = 10 };
        var (diario, pasta) = DiarioTemporario(opcoes);
        var chegou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repositorio = new RepositorioRoteirizado(async (chamada, _, token) =>
        {
            if (chamada == 1)
            {
                chegou.SetResult();
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                throw new InvalidOperationException("conexão encerrada pelo desligamento (simulado)");
            }
        });
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        try
        {
            using var gravador = new GravadorAuditoriaHttp(diario, repositorio, Microsoft.Extensions.Options.Options.Create(opcoes), relogio,
                logs.CreateLogger<GravadorAuditoriaHttp>());
            await diario.RegistrarAsync(Registro());
            await gravador.StartAsync(CancellationToken.None);
            await relogio.AguardarTimerAsync();
            relogio.Disparar();
            await chegou.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await gravador.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(gravador.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Contains(logs.Entries, e => e.StartsWith("Error Falha inesperada no gravador da auditoria HTTP (System.InvalidOperationException)",
                StringComparison.Ordinal));
            Assert.Equal(2, repositorio.Chamadas);
        }
        finally
        {
            diario.Dispose();
            Directory.Delete(pasta, recursive: true);
        }
    }

    [Fact]
    public async Task Gravador_EncerramentoSemTempoParaAUltimaGravacao_DeixaOsPendentesEmDisco_EAvisa()
    {
        var opcoes = new AuditoriaHttpOptions();
        var (diario, pasta) = DiarioTemporario(opcoes);
        var repositorio = new RepositorioRoteirizado((_, _, _) => Task.CompletedTask);
        var logs = new CapturingLoggerFactory();
        try
        {
            using var gravador = new GravadorAuditoriaHttp(diario, repositorio, Microsoft.Extensions.Options.Options.Create(opcoes), TimeProvider.System,
                logs.CreateLogger<GravadorAuditoriaHttp>());
            await diario.RegistrarAsync(Registro());

            await gravador.StopAsync(new CancellationToken(canceled: true));

            Assert.Equal(0, repositorio.Chamadas);
            Assert.Contains(logs.Entries, e => e.StartsWith("Warning Última gravação da auditoria HTTP no encerramento não terminou (TaskCanceledException)",
                StringComparison.Ordinal));
            Assert.NotEmpty(Directory.GetFiles(pasta)); // o registro continua no segmento do diário, para o próximo início
        }
        finally
        {
            diario.Dispose();
            Directory.Delete(pasta, recursive: true);
        }
    }

    // --- Contratos dos streams da captura e da política ---

    [Fact]
    public void LeituraAuditada_EhSoLeituraSemPosicao()
    {
        using var leitura = new LeituraAuditadaStream(new MemoryStream([1, 2, 3]), new CapturaCorpoHttp(1024, null));
        Assert.Equal((true, false, false), (leitura.CanRead, leitura.CanSeek, leitura.CanWrite));
        Assert.Throws<NotSupportedException>(() => leitura.Length);
        Assert.Throws<NotSupportedException>(() => leitura.Position);
        Assert.Throws<NotSupportedException>(() => leitura.Position = 1);
        Assert.Throws<NotSupportedException>(() => leitura.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => leitura.SetLength(1));
        Assert.Throws<NotSupportedException>(() => leitura.Write([1], 0, 1));
        leitura.Flush();
        Assert.Equal(3, leitura.Read(new byte[8], 0, 8));
    }

    private sealed class CorpoDeRespostaDeTeste : IHttpResponseBodyFeature
    {
        public Stream Stream { get; } = new MemoryStream();
        public PipeWriter Writer => PipeWriter.Create(Stream);
        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync() => Task.CompletedTask;
    }

    [Fact]
    public void EscritaAuditada_EhSoEscritaSemPosicao()
    {
        using var escrita = new EscritaAuditadaStream(new CorpoDeRespostaDeTeste(), () => new CapturaCorpoHttp(1024, null));
        Assert.Equal((false, false, true), (escrita.CanRead, escrita.CanSeek, escrita.CanWrite));
        Assert.Throws<NotSupportedException>(() => escrita.Length);
        Assert.Throws<NotSupportedException>(() => escrita.Position);
        Assert.Throws<NotSupportedException>(() => escrita.Position = 1);
        Assert.Throws<NotSupportedException>(() => escrita.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => escrita.SetLength(1));
        Assert.Throws<NotSupportedException>(() => escrita.Read(new byte[1], 0, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Politica_CaminhoVazio_EhARaiz(string? caminho) => Assert.Equal("/", PoliticaAuditoriaHttp.Normalizar(caminho));
}
