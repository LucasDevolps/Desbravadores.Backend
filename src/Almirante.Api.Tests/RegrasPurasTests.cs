using System.Net;
using System.Reflection;
using System.Text.Json;
using Almirante.Api.Controllers;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Tests;

/// <summary>Regras puras e configurações cujos ramos não dependem de banco nem de rede.</summary>
public sealed class RegrasPurasTests
{
    private static JwtOptions Jwt(string chave = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=") => new()
    {
        Issuer = "emissor", Audience = "publico", ActiveKeyId = "v1", AccessTokenMinutes = 10, Keys = new() { ["v1"] = chave },
    };

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    [Fact]
    public void Jwt_AcessoNuncaValeMaisQueOLimiteAbsolutoDaSessao()
    {
        var agora = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var servico = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(Jwt()), new RelogioFixo(agora));

        Assert.Equal(agora.UtcDateTime.AddMinutes(3), servico.GenerateToken(Guid.NewGuid(), "DS", Guid.NewGuid(), agora.UtcDateTime.AddMinutes(3)).ExpiresAtUtc);
        Assert.Equal(agora.UtcDateTime.AddMinutes(10), servico.GenerateToken(Guid.NewGuid(), "DS", Guid.NewGuid(), agora.UtcDateTime.AddDays(1)).ExpiresAtUtc);
    }

    [Fact]
    public void Jwt_KidDesconhecidoEChaveIlegivel_SaoRecusados()
    {
        Assert.Throws<SecurityTokenInvalidSigningKeyException>(() => JwtKeySet.GetKey(Jwt(), "v9"));
        var erro = Assert.Throws<OptionsValidationException>(() => JwtKeySet.GetKey(Jwt("isto não é base64"), "v1"));
        Assert.StartsWith("Jwt:Keys:v1: ", Assert.Single(erro.Failures));
    }

    [Fact]
    public void Jwt_EmissorEAudienciaSaoObrigatorios()
    {
        var opcoes = Jwt();
        opcoes.Issuer = " ";
        opcoes.Audience = "";
        var falhas = new JwtOptionsValidator().Validate(null, opcoes).Failures!;
        Assert.Contains("Jwt:Issuer é obrigatório.", falhas);
        Assert.Contains("Jwt:Audience é obrigatório.", falhas);
    }

    [Fact]
    public void Placeholders_TipoSanguineo_EIpDeOrigem_TratamAusencia()
    {
        Assert.False(SecretPlaceholders.IsPlaceholder(null));
        Assert.True(SecretPlaceholders.IsPlaceholder("DEFINA_A_CHAVE"));
        Assert.False(TipoSanguineo.EhValido(null));
        Assert.True(TipoSanguineo.EhValido("AB-"));

        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.4");
        Assert.Equal("203.0.113.4", contexto.IpDeOrigem());
        contexto.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::1");
        Assert.Equal("2001:db8::1", contexto.IpDeOrigem());
    }

    [Fact]
    public void TipoFluxo_NuloNoJson_EhRecusado()
    {
        var opcoes = new JsonSerializerOptions { Converters = { new TipoFluxoLancamentoJsonConverter(false) } };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TipoFluxoLancamento>("null", opcoes));
        Assert.Equal(TipoFluxoLancamento.Saida, JsonSerializer.Deserialize<TipoFluxoLancamento>("\" Saida \"", opcoes));
    }

    [Fact]
    public void ValoresMaximos_CabemEmDecimal18Com2Casas()
    {
        // const decimal vira campo inicializado no construtor estático: lido por reflexão, como a cultura de quem consome a regra.
        static decimal Ler(Type tipo) => (decimal)tipo.GetField("ValorMaximo", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(9_999_999_999_999_999.99m, Ler(typeof(EventoRegras)));
        Assert.Equal(Ler(typeof(EventoRegras)), Ler(typeof(RegrasAlmoxarifado)));
        Assert.True(EventoRegras.ValorValido(EventoRegras.ValorMaximo));
        Assert.False(EventoRegras.ValorValido(EventoRegras.ValorMaximo + 0.01m));
    }

    [Fact]
    public void DescricaoDoLancamentoDeEvento_TemNoMaximo500Caracteres()
    {
        var descricao = EventoRegras.DescricaoLancamento(new string('x', 600), new DateOnly(2026, 11, 10));
        Assert.Equal(500, descricao.Length);
        Assert.StartsWith("Evento - xxx", descricao);
        Assert.Equal("Evento - Parque - 10/11/2026", EventoRegras.DescricaoLancamento("Parque", new DateOnly(2026, 11, 10)));
    }

    [Fact]
    public void DescricaoDeEnum_SemValorDefinido_UsaONumero()
    {
        Assert.Equal("Disponível", RegrasAlmoxarifado.Descricao(SituacaoBem.Disponivel));
        Assert.Equal("99", RegrasAlmoxarifado.Descricao((SituacaoBem)99));
    }

    [Fact]
    public void NotificacaoDeEvento_SoParaLancamentosDoCadastroComMembro_EValorNegativoComSinal()
    {
        var evento = new Evento { Id = Guid.NewGuid(), EventoGrupoId = Guid.NewGuid(), Local = "Parque", DataEvento = new DateOnly(2026, 11, 10) };
        Lancamento Lancamento(Guid? membro, Guid? eventoId) => new()
        {
            Id = Guid.NewGuid(), MembroId = membro, EventoId = eventoId, Finalidade = null, Descricao = "x", Categoria = CategoriaLancamento.Evento,
            Valor = 20m, Vencimento = evento.DataEvento, Status = StatusLancamento.Pendente, TipoFluxo = TipoFluxoLancamento.Entrada, Ativo = true,
        };
        var membro = Guid.NewGuid();

        var notificacoes = NotificacoesFabrica.ParaEvento(evento,
            [Lancamento(membro, evento.Id), Lancamento(null, evento.Id), Lancamento(Guid.NewGuid(), Guid.NewGuid())], DateTime.UtcNow);

        Assert.Equal(membro, Assert.Single(notificacoes).DestinatarioId);
        Assert.Equal("-R$ 1.234,50", NotificacoesFabrica.Moeda(-1234.5m));
        Assert.Equal("R$ 0,00", NotificacoesFabrica.Moeda(0m));
    }

    [Fact]
    public async Task ErrosDeModelBinding_SemMensagemUsamAExcecaoSanitizada()
    {
        await using var factory = new AlmiranteApiFactory();
        using var escopo = factory.Services.CreateScope();
        var contexto = new ActionContext(new DefaultHttpContext { RequestServices = escopo.ServiceProvider }, new RouteData(), new ActionDescriptor());
        contexto.ModelState.AddModelError("semNada", "");
        contexto.ModelState.TryAddModelException("comExcecao", new FormatException("The JSON value could not be converted to System.Guid. Path: $.id"));

        var resultado = Assert.IsType<BadRequestObjectResult>(ModelBindingErrors.Responder(contexto));
        var problema = Assert.IsType<ValidationProblemDetails>(resultado.Value);
        Assert.True(problema.Errors.ContainsKey("semNada"));
        Assert.DoesNotContain("System.Guid", string.Join(" ", problema.Errors["comExcecao"]));
    }

    // --- Filtros OpenAPI: metadados ausentes ou já preenchidos não são sobrescritos ---

    private static OperationFilterContext Contexto(Type controller, string? caminho, params object[] metadados)
    {
        var acao = new ControllerActionDescriptor { ControllerTypeInfo = controller.GetTypeInfo(), EndpointMetadata = [.. metadados] };
        var descricao = new ApiDescription { ActionDescriptor = acao, HttpMethod = "POST", RelativePath = caminho };
        return new OperationFilterContext(descricao, null!, new SchemaRepository(), null!, null!);
    }

    [Fact]
    public void FiltrosOpenApi_PreservamResumoExistente_EToleramOperacaoSemParametros()
    {
        var operacao = new OpenApiOperation { Summary = "resumo do XML", Description = "descrição do XML" };
        new AlmoxarifadoOperationFilter().Apply(operacao, Contexto(typeof(AlmoxarifadoBensController), null,
            new EndpointSummaryAttribute("do atributo"), new EndpointDescriptionAttribute("do atributo")));
        Assert.Equal(("resumo do XML", "descrição do XML"), (operacao.Summary, operacao.Description));

        var semMetadados = new OpenApiOperation();
        new AlmoxarifadoOperationFilter().Apply(semMetadados, Contexto(typeof(AlmoxarifadoBensController), "api/Almoxarifado/Bens"));
        Assert.Null(semMetadados.Summary);

        var evento = new OpenApiOperation { Summary = "resumo do XML" };
        new EventosOperationFilter().Apply(evento, Contexto(typeof(EventosController), "api/Eventos", new EndpointSummaryAttribute("do atributo")));
        Assert.Equal("resumo do XML", evento.Summary);
        Assert.Null(evento.Description);
    }

    // --- Configurações lidas antes do Build ---

    private sealed class FactoryComConfiguracaoDeHost(IDictionary<string, string> configuracao) : AlmiranteApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            foreach (var (chave, valor) in configuracao) builder.UseSetting(chave, valor);
        }
    }

    [Fact]
    public async Task SemAppsettings_CorsUsaAsOrigensLocaisPadrao()
    {
        var pastaVazia = Directory.CreateTempSubdirectory("almirante-sem-appsettings-").FullName;
        try
        {
            await using var factory = new FactoryComConfiguracaoDeHost(new Dictionary<string, string> { [WebHostDefaults.ContentRootKey] = pastaVazia });
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/Auth/csrf");
            preflight.Headers.Add("Origin", "http://localhost:4201");
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            var resposta = await factory.CreateClient().SendAsync(preflight);
            Assert.Equal("http://localhost:4201", resposta.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
        finally
        {
            Directory.Delete(pastaVazia, recursive: true);
        }
    }

    [Fact]
    public async Task ComEndpointOtlp_ATelemetriaEhExportada_SemAfetarAApi()
    {
        await using var factory = new FactoryComConfiguracaoDeHost(new Dictionary<string, string> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317" });
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/alive")).StatusCode);
        var exportador = factory.Services.GetRequiredService<IOptionsMonitor<OpenTelemetry.Exporter.OtlpExporterOptions>>();
        Assert.Equal(new Uri("http://127.0.0.1:4317"), exportador.CurrentValue.Endpoint);
    }

    [Fact]
    public async Task Hsts_ComSubdominiosEPreload_ConfiguradosEmProducao()
    {
        await using var factory = new ConfiguredApiFactory("Production", new Dictionary<string, string?>
        {
            ["Hsts:IncludeSubDomains"] = "true", ["Hsts:Preload"] = "true", ["AllowedHosts"] = "*",
        });
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/alive");
        requisicao.Headers.Host = "api.exemplo.com";
        var resposta = await factory.CreateClient().SendAsync(requisicao);
        Assert.Equal("max-age=31536000; includeSubDomains; preload", resposta.Headers.GetValues("Strict-Transport-Security").Single());
    }
}
