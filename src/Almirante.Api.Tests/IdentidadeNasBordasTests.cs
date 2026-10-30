using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Almirante.Api.Controllers;
using Almirante.Api.Dtos;
using Almirante.Api.Hubs;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

/// <summary>
/// Bordas de identidade que o JwtBearer da API nunca produz (ele sempre entrega sub, sid e role validados), mas que os controllers e o
/// hub tratam de forma explícita: sem esses dados, nada é executado em nome de ninguém.
/// </summary>
public sealed class IdentidadeNasBordasTests
{
    private abstract class IdentidadeDeTeste(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected abstract IEnumerable<Claim> Claims { get; }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(Claims, "Teste", "sub", "role")), "Teste")));
    }

    private sealed class SemIdentificador(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : IdentidadeDeTeste(o, l, e)
    {
        protected override IEnumerable<Claim> Claims => [new("role", "DIR")];
    }

    private sealed class UsuarioInexistente(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : IdentidadeDeTeste(o, l, e)
    {
        protected override IEnumerable<Claim> Claims => [new("role", "DIR"), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())];
    }

    private static ConfiguredApiFactory Fabrica<THandler>() where THandler : IdentidadeDeTeste =>
        new(services: s => s.AddAuthentication("Teste").AddScheme<AuthenticationSchemeOptions, THandler>("Teste", _ => { }));

    [Fact]
    public async Task AuthMe_SemIdOuDeUsuarioQueNaoExisteMais_Retorna401()
    {
        await using var semId = Fabrica<SemIdentificador>();
        Assert.Equal(HttpStatusCode.Unauthorized, (await semId.CreateClient().GetAsync("/api/Auth/Me")).StatusCode);

        await using var inexistente = Fabrica<UsuarioInexistente>();
        Assert.Equal(HttpStatusCode.Unauthorized, (await inexistente.CreateClient().GetAsync("/api/Auth/Me")).StatusCode);
    }

    [Fact]
    public async Task RefreshSemCookie_Retorna401_ELogoutAnonimo_RemoveOCookieSemErro()
    {
        await using var factory = new AlmiranteApiFactory();
        var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/Auth/refresh", null)).StatusCode);
        var logout = await client.PostAsync("/api/Auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CatalogoDeFormacao_ClasseOuEspecialidadeInexistente_Retorna404()
    {
        await using var factory = new AlmiranteApiFactory();
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        // Classe fora do enum é recusada já no model binding (400); o 404 de classe é coberto no controller, abaixo.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/Classes/999")).StatusCode);
        foreach (var rota in new[] { $"/api/Especialidades/{Guid.NewGuid()}", $"/api/Especialidades/{Guid.NewGuid()}/Revisoes" })
        {
            var resposta = await client.GetAsync(rota);
            Assert.True(resposta.StatusCode == HttpStatusCode.NotFound, $"{rota}: {(int)resposta.StatusCode}");
        }
    }

    // --- Hub e provedor de usuário do SignalR ---

    private sealed class ContextoDeHub(ClaimsPrincipal? usuario) : HubCallerContext
    {
        public bool Abortado { get; private set; }
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => usuario;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Abortado = true;
    }

    private static ClaimsPrincipal Usuario(params Claim[] claims) => new(new ClaimsIdentity(claims, "Teste", "sub", "role"));

    public static TheoryData<string, Claim[]?> IdentidadesIncompletas => new()
    {
        { "sem usuário", null },
        { "sem id", [new Claim("sid", Guid.NewGuid().ToString()), new Claim("role", "DS")] },
        { "sem sessão", [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("role", "DS")] },
        { "sessão inválida", [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("sid", "x"), new Claim("role", "DS")] },
        { "sem cargo", [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("sid", Guid.NewGuid().ToString())] },
    };

    [Theory]
    [MemberData(nameof(IdentidadesIncompletas))]
    public async Task Hub_ConexaoComIdentidadeIncompleta_EhEncerradaSemRegistro(string caso, Claim[]? claims)
    {
        var conexoes = new ConexoesNotificacoes();
        var contexto = new ContextoDeHub(claims is null ? null : Usuario(claims));
        using var hub = new NotificacoesHub(conexoes, NullLogger<NotificacoesHub>.Instance) { Context = contexto };

        await hub.OnConnectedAsync();

        Assert.True(contexto.Abortado, caso);
        Assert.Empty(conexoes.Abertas());
    }

    [Fact]
    public async Task Hub_ConexaoCompleta_EhRegistradaEEncerrarARemove()
    {
        var conexoes = new ConexoesNotificacoes();
        var usuarioId = Guid.NewGuid();
        var contexto = new ContextoDeHub(Usuario(new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()),
            new Claim("sid", Guid.NewGuid().ToString()), new Claim("role", "DS")));
        using var hub = new NotificacoesHub(conexoes, NullLogger<NotificacoesHub>.Instance) { Context = contexto };

        await hub.OnConnectedAsync();
        Assert.Equal(usuarioId, Assert.Single(conexoes.Abertas()).UsuarioId);
        Assert.False(contexto.Abortado);

        await hub.OnDisconnectedAsync(null);
        Assert.Empty(conexoes.Abertas());
    }

    [Fact]
    public void ProvedorDeUsuario_SemIdNaoRoteiaParaNinguem()
    {
        static HubConnectionContext Conexao(ClaimsPrincipal usuario) =>
            new(new DefaultConnectionContext { User = usuario }, new HubConnectionContextOptions(), NullLoggerFactory.Instance);
        var provedor = new UsuarioIdProvider();
        var id = Guid.NewGuid();

        Assert.Null(provedor.GetUserId(Conexao(Usuario(new Claim("role", "DS")))));
        Assert.Equal(id.ToString("D"), provedor.GetUserId(Conexao(Usuario(new Claim(ClaimTypes.NameIdentifier, id.ToString("N"))))));
    }

    // --- Controller de lançamentos: resultado que o serviço não deveria produzir ---

    private sealed class SenderFixo(object resposta) : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)resposta);
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(resposta);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RegistrarLancamento_ResultadoDesconhecidoDoServico_FalhaEmVezDeResponderAlgoInventado()
    {
        var controller = new LancamentosController(new SenderFixo(new RegistrarLancamentoResult((RegistrarLancamentoOutcome)99, null, null)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Usuario(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())) },
            },
        };

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Registrar(null, new RegistrarLancamentoRequest { Finalidade = "Mensalidade", Categoria = Almirante.Api.Entities.CategoriaLancamento.Clube }, CancellationToken.None));
        Assert.Equal("Resultado inesperado: 99.", erro.Message);
    }

    [Fact]
    public async Task Classes_QuandoOServicoNaoAchaAClasse_Responde404()
    {
        var controller = new ClassesController(new SenderFixo(null!))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Usuario(new Claim("role", "DS")) } },
        };

        var classe = Assert.IsType<NotFoundObjectResult>((await controller.Obter((Almirante.Api.Entities.ClasseDesbravador)999, CancellationToken.None)).Result);
        var revisoes = Assert.IsType<NotFoundObjectResult>((await controller.ListarRevisoes((Almirante.Api.Entities.ClasseDesbravador)999, CancellationToken.None)).Result);
        Assert.All(new[] { classe, revisoes }, r => Assert.Equal("Classe não encontrada.", Assert.IsType<ProblemDetails>(r.Value).Title));
    }
}
