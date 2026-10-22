using System.Security.Claims;
using Almirante.Api.Dtos;
using Almirante.Api.Features.Autoatendimento;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

public sealed class PermissoesUnitTests
{
    private static readonly string[] TodosOsCargos =
        ["ADM", "DIR", "DIRA", "SEC", "TES", "DS", "CAP", "CONS", "CONSA", "INST", "CPT", "SECU", "TESU", "ALM", "PAD", "COREC", "CAPU"];

    [Fact]
    public void Roles_SeparamCadastrosFinanceiroEGestao_SemConfundirTesoureiroComTesoureiroDeUnidade()
    {
        Assert.Equal(["ADM", "DIR", "DIRA"], Roles.Diretoria);
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC"], Roles.Cadastros);
        Assert.Equal(["ADM", "DIR", "DIRA", "TES"], Roles.Financeiro);
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC", "TES"], Roles.Gestao);
        Assert.Equal(["DIR", "DIRA", "SEC", "TES"], Roles.CargosDeGestao);
        Assert.DoesNotContain("TESU", Roles.Financeiro.Concat(Roles.Gestao).Concat(Roles.CargosDeGestao));
        Assert.DoesNotContain("SECU", Roles.Cadastros.Concat(Roles.Gestao).Concat(Roles.CargosDeGestao));
    }

    /// <summary>Tabela-verdade completa de quem gerencia quem (cadastro, edição, exclusão, foto e cargo atribuído).</summary>
    [Fact]
    public void PodeGerenciar_TabelaCompleta()
    {
        foreach (var solicitante in TodosOsCargos)
        foreach (var alvo in TodosOsCargos)
        {
            var esperado = alvo switch
            {
                "ADM" => solicitante == "ADM",
                "DIR" or "DIRA" or "SEC" or "TES" => solicitante is "ADM" or "DIR" or "DIRA",
                _ => true,
            };
            Assert.True(esperado == Roles.PodeGerenciar(solicitante, alvo), $"{solicitante} → {alvo}: esperado {esperado}");
        }
    }

    [Theory]
    [InlineData("ADM", new[] { "GestaoCadastros", "GestaoFinanceira", "GestaoEventos", "ConsultaMembros", "Painel", "Autoatendimento" })]
    [InlineData("DIR", new[] { "GestaoCadastros", "GestaoFinanceira", "GestaoEventos", "ConsultaMembros", "Painel", "Autoatendimento" })]
    [InlineData("DIRA", new[] { "GestaoCadastros", "GestaoFinanceira", "GestaoEventos", "ConsultaMembros", "Painel", "Autoatendimento" })]
    [InlineData("SEC", new[] { "GestaoCadastros", "GestaoEventos", "ConsultaMembros", "Painel", "Autoatendimento" })]
    [InlineData("TES", new[] { "GestaoFinanceira", "GestaoEventos", "ConsultaMembros", "Painel", "Autoatendimento" })]
    [InlineData("DS", new[] { "Autoatendimento" })]
    [InlineData("TESU", new[] { "Autoatendimento" })]
    [InlineData("ALM", new[] { "Autoatendimento" })]
    public async Task Policies_ConcedemSomenteOsModulosDoCargo(string role, string[] esperadas)
    {
        var services = new ServiceCollection().AddLogging().AddAuthorization(Policies.Registrar).BuildServiceProvider();
        var autorizacao = services.GetRequiredService<IAuthorizationService>();
        var usuario = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", role)], "Bearer", "sub", "role"));
        var anonimo = new ClaimsPrincipal(new ClaimsIdentity());
        string[] todas = [Policies.GestaoCadastros, Policies.GestaoFinanceira, Policies.GestaoEventos, Policies.ConsultaMembros, Policies.Painel, Policies.Autoatendimento];

        var concedidas = new List<string>();
        foreach (var policy in todas)
        {
            if ((await autorizacao.AuthorizeAsync(usuario, policy)).Succeeded) concedidas.Add(policy);
            Assert.False((await autorizacao.AuthorizeAsync(anonimo, policy)).Succeeded);
        }

        Assert.Equal(esperadas, concedidas);
    }

    [Fact]
    public void ClaimsPrincipal_UsuarioIdERole_VemSoDoTokenValidado()
    {
        var id = Guid.NewGuid();
        var autenticado = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim("role", "DS")], "Bearer"));
        var semId = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "DS")], "Bearer"));

        Assert.Equal((id, "DS"), (autenticado.UsuarioId(), autenticado.Role()));
        var erro = Assert.Throws<ApiProblemException>(() => semId.UsuarioId());
        Assert.Equal((StatusCodes.Status401Unauthorized, "Identidade autenticada inválida."), (erro.Status, erro.Title));
        Assert.Equal(string.Empty, new ClaimsPrincipal(new ClaimsIdentity()).Role());
    }

    [Fact]
    public async Task ObterMeusDados_UsuarioQueDeixouDeExistirOuFicouInativo_404()
    {
        using var factory = new AlmiranteApiFactory();
        using var scope = factory.Services.CreateScope();
        var handler = new ObterMeusDadosHandler(scope.ServiceProvider.GetRequiredService<UsuariosService>());

        var erro = await Assert.ThrowsAsync<ApiProblemException>(() => handler.Handle(new ObterMeusDadosQuery(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal((StatusCodes.Status404NotFound, "Usuário não encontrado."), (erro.Status, erro.Title));
    }
}
