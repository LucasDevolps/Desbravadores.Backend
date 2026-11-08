using System.Net;
using System.Net.Http.Json;
using System.Text;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Almirante.Api.Tests.SenhasTestKit;

namespace Almirante.Api.Tests;

/// <summary>Contrato HTTP das rotas de senha (issue #159) sem SQL Server: autenticação, CSRF, no-store, limites e higiene das respostas.</summary>
public sealed class SenhasApiTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static readonly string[] RotasAnonimas = ["/api/Auth/recuperacao-senha/solicitar", "/api/Auth/recuperacao-senha/confirmar"];

    private static string[] RotasAutenticadas(Guid alvo) =>
        ["/api/Auth/alterar-senha", $"/api/Usuarios/{alvo}/alterar-senha", $"/api/Usuarios/{alvo}/recuperacao-senha/autorizacao"];

    [Fact]
    public async Task SemSessao_RotasAutenticadas_401ComNoStore()
    {
        using var anonimo = await AnonimoAsync(factory);
        foreach (var rota in RotasAutenticadas(Guid.NewGuid()))
        {
            using var resposta = await anonimo.PostAsJsonAsync(rota, new { });
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
            AssertNoStore(resposta);
        }
        using var lista = await anonimo.GetAsync("/api/Usuarios/recuperacao-senha/pendentes");
        Assert.Equal(HttpStatusCode.Unauthorized, lista.StatusCode);
    }

    [Fact]
    public async Task SemTokenCsrf_TodasAsRotasPost_400ComNoStore()
    {
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        foreach (var rota in RotasAutenticadas(Guid.NewGuid()).Concat(RotasAnonimas))
        {
            using var resposta = await admin.PostAsJsonAsync(rota, new { senhaAtualSolicitante = AlmiranteApiFactory.AdminSenha });
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            AssertNoStore(resposta);
        }

        using var anonimo = factory.CreateClient();
        foreach (var rota in RotasAnonimas)
        {
            using var resposta = await anonimo.PostAsJsonAsync(rota, new { email = "pessoa@clube.org" });
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        }
    }

    [Theory]
    [InlineData("TES")]
    [InlineData("DIRA")]
    [InlineData("DS")]
    public async Task CargoSemPermissao_RotasDeTerceiros_403ComNoStore_SemDistinguirAlvoInexistente(string role)
    {
        var (client, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);
        await TestHelpers.AddCsrfAsync(client);
        var existente = await TestHelpers.AddUsuarioAsync(factory, "Alvo", "DS");

        foreach (var alvo in new[] { existente.Id, Guid.NewGuid() })
        {
            foreach (var rota in RotasAutenticadas(alvo).Skip(1))
            {
                using var resposta = await client.PostAsJsonAsync(rota, new { senhaAtualSolicitante = TestHelpers.SenhaPadraoTeste, metodoVerificacao = "AtendimentoPresencial" });
                Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
                AssertNoStore(resposta);
            }
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/Usuarios/recuperacao-senha/pendentes")).StatusCode);
    }

    [Theory]
    [InlineData("usuarioResponsavelId")]
    [InlineData("role")]
    [InlineData("ip")]
    [InlineData("alteradaEmUtc")]
    [InlineData("senhaAtual")]
    public async Task CamposForaDoContrato_SaoRecusados_SemEcoarASenha(string campo)
    {
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var segredo = NovaSenha();
        var corpo = $$"""{"senhaAtualSolicitante":"{{segredo}}","novaSenha":"{{segredo}}","confirmacaoNovaSenha":"{{segredo}}","{{campo}}":"x"}""";

        foreach (var rota in RotasAutenticadas(Guid.NewGuid()))
        {
            using var resposta = await admin.PostAsync(rota, new StringContent(corpo, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
            var texto = await resposta.Content.ReadAsStringAsync();
            Assert.Contains("Campo não aceito nesta operação.", texto);
            Assert.DoesNotContain(segredo, texto);
        }
    }

    [Fact]
    public async Task JsonMalFormadoOuTipoErrado_NaoEcoaSenhaNemNaRespostaNemNosLogs()
    {
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var segredo = NovaSenha();

        foreach (var corpo in new[]
        {
            $$"""{"senhaAtualSolicitante":"{{segredo}}","novaSenha":123}""",
            $$"""{"senhaAtualSolicitante":"{{segredo}}","novaSenha":"{{segredo}}" """,
            $$"""{"token":"{{segredo}}","novaSenha":["{{segredo}}"]}""",
        })
        {
            foreach (var rota in RotasAutenticadas(Guid.NewGuid()).Append(RotasAnonimas[1]))
            {
                using var resposta = await admin.PostAsync(rota, new StringContent(corpo, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
                Assert.DoesNotContain(segredo, await resposta.Content.ReadAsStringAsync());
            }
        }

        Assert.DoesNotContain(factory.Logs.Entries, e => e.Contains(segredo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidacaoEstrutural_400AntesDeQualquerConsulta_ComCamposDoContrato()
    {
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var segredo = NovaSenha();

        using var propria = await AlterarPropriaAsync(admin, "", segredo, segredo + "!");
        Assert.Equal(HttpStatusCode.BadRequest, propria.StatusCode);
        Assert.Equal(["confirmacaoNovaSenha", "senhaAtualSolicitante"], (await CamposComErroAsync(propria)).Order());

        using var fraca = await AlterarDeUsuarioAsync(admin, Guid.NewGuid(), AlmiranteApiFactory.AdminSenha, "curta");
        Assert.Equal(["novaSenha"], await CamposComErroAsync(fraca));

        using var anonimo = await AnonimoAsync(factory);
        using var confirmar = await anonimo.PostAsJsonAsync(RotasAnonimas[1], new { novaSenha = segredo, confirmacaoNovaSenha = segredo });
        Assert.Equal(["token"], await CamposComErroAsync(confirmar));
        using var solicitar = await SolicitarAsync(anonimo, "nao-e-email");
        Assert.Equal(["email"], await CamposComErroAsync(solicitar));
        AssertNoStore(solicitar);
        Assert.DoesNotContain(segredo, string.Join("\n", factory.Logs.Entries));
    }

    [Fact]
    public async Task SemSqlServer_OperacoesTransacionaisSaoRecusadasSemAlterarNada()
    {
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Membro", "DS");
        var segredo = NovaSenha();
        var antes = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Id == membro.Id).Select(u => u.SenhaHash).SingleAsync());

        using var propria = await AlterarPropriaAsync(admin, AlmiranteApiFactory.AdminSenha, segredo);
        using var terceiro = await AlterarDeUsuarioAsync(admin, membro.Id, AlmiranteApiFactory.AdminSenha, segredo);
        using var emissao = await EmitirAsync(admin, membro.Id, AlmiranteApiFactory.AdminSenha);
        using var anonimo = await AnonimoAsync(factory);
        using var confirmar = await ConfirmarAsync(anonimo, new string('a', 43), segredo);

        Assert.All(new[] { propria, terceiro, emissao, confirmar }, r => Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode));
        Assert.Equal(antes, await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Id == membro.Id).Select(u => u.SenhaHash).SingleAsync()));
    }

    [Fact]
    public async Task Solicitar_EPendentes_FuncionamNoProvedorEmMemoria()
    {
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Pedido", "DS");
        using var anonimo = await AnonimoAsync(factory);
        Assert.Equal(HttpStatusCode.Accepted, (await SolicitarAsync(anonimo, membro.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SolicitarAsync(anonimo, membro.Email)).StatusCode);

        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var pendentes = await admin.GetFromJsonAsync<RecuperacoesSenhaPendentesResponse>(
            "/api/Usuarios/recuperacao-senha/pendentes?pageSize=50", NotificacoesTestKit.Json);
        var item = Assert.Single(pendentes!.Items, i => i.UsuarioId == membro.Id);
        Assert.Equal((2, StatusRecuperacaoSenha.Pendente, "DS"), (item.Solicitacoes, item.Status, item.Cargo.Role));
    }

    [Fact]
    public void HasherConfigurado_ComOCustoRevisado()
    {
        Assert.Equal(PasswordPolicy.IteracoesHash, factory.Services.GetRequiredService<IOptions<PasswordHasherOptions>>().Value.IterationCount);
        var hash = factory.Services.GetRequiredService<IPasswordHasher<Usuario>>()
            .HashPassword(new Usuario { Nome = "", Email = "", EmailNormalizado = "", SenhaHash = "" }, NovaSenha());
        Assert.Equal(PasswordPolicy.IteracoesHash, IteracoesDoHash(hash));
    }
}

/// <summary>Limites de tentativas com valores baixos (fábrica própria: os padrões dos outros testes são altos).</summary>
public sealed class SenhasLimitesApiTests
{
    [Fact]
    public async Task RecuperacaoSemLogin_LimitePorIp_429ComRetryAfterNoStoreEMensagemPropria()
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Senhas:RecuperacoesPorIp"] = "2" });
        using var anonimo = await AnonimoAsync(factory, ip: "192.0.2.200");
        using var outroIp = await AnonimoAsync(factory, ip: "192.0.2.201");

        Assert.Equal(HttpStatusCode.Accepted, (await SolicitarAsync(anonimo, "pessoa@clube.org")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmarAsync(anonimo, "", "x")).StatusCode);
        using var excesso = await SolicitarAsync(anonimo, "pessoa@clube.org");

        Assert.Equal(HttpStatusCode.TooManyRequests, excesso.StatusCode);
        AssertNoStore(excesso);
        Assert.True(int.Parse(excesso.Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Equal("Muitas tentativas de recuperação de senha.", await TituloAsync(excesso));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ConfirmarAsync(anonimo, new string('a', 43), NovaSenha())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SolicitarAsync(outroIp, "pessoa@clube.org")).StatusCode);
    }

    [Fact]
    public async Task OperacoesAutenticadas_LimitePorResponsavel_429_NaoPenalizaOAlvo()
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Senhas:TentativasPorUsuario"] = "2" });
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var alvo = await TestHelpers.AddUsuarioAsync(factory, "Alvo", "DS");

        Assert.Equal(HttpStatusCode.BadRequest, (await AlterarDeUsuarioAsync(admin, alvo.Id, "", NovaSenha())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AlterarPropriaAsync(admin, "", NovaSenha())).StatusCode);
        using var excesso = await EmitirAsync(admin, alvo.Id, AlmiranteApiFactory.AdminSenha);
        using var propria = await AlterarPropriaAsync(admin, AlmiranteApiFactory.AdminSenha, NovaSenha());

        Assert.Equal(HttpStatusCode.TooManyRequests, excesso.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, propria.StatusCode);
        using var administrativa = await AlterarDeUsuarioAsync(admin, alvo.Id, AlmiranteApiFactory.AdminSenha, NovaSenha());
        Assert.Equal(HttpStatusCode.TooManyRequests, administrativa.StatusCode);
        Assert.NotNull(administrativa.Headers.RetryAfter);
        AssertNoStore(excesso);
        Assert.Equal("Muitas tentativas de operações de senha.", await TituloAsync(excesso));
        Assert.Equal("Muitas tentativas de alteração de senha.", await TituloAsync(propria));
        var estado = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == alvo.Id));
        Assert.Equal((0, (DateTime?)null), (estado.FalhasLoginConsecutivas, estado.LoginBloqueadoAteUtc));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(factory, alvo.Email, TestHelpers.SenhaPadraoTeste));
    }
}
