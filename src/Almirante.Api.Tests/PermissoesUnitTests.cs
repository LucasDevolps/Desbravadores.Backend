using System.Reflection;
using System.Security.Claims;
using Almirante.Api.Data.Migrations;
using Almirante.Api.Dtos;
using Almirante.Api.Features.Autoatendimento;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>
/// RBAC dinâmico (issue #182) sem pipeline HTTP: catálogo, matriz inicial equivalente às listas estáticas anteriores (issues #127, #159,
/// #125 e #126), migration congelada, cobertura de todas as actions por uma permissão do catálogo e decisões do handler lidas do banco.
/// </summary>
public sealed class PermissoesUnitTests
{
    private static readonly string[] TodosOsCargos = MatrizInicialAutorizacao.CargosConhecidos;

    private static string[] ComPermissao(string chave) =>
        [.. TodosOsCargos.Where(r => MatrizInicialAutorizacao.Concessoes.TryGetValue(r, out var c) && c.Contains(chave))];

    [Fact]
    public void Catalogo_ChavesEIdsUnicos_ReservadasSoAAutorizacao_RegraPorAlvoSoNasOperacoesSobreOutroUsuario()
    {
        Assert.Equal(CatalogoPermissoes.Todas.Count, CatalogoPermissoes.Todas.Select(p => p.Chave).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(CatalogoPermissoes.Todas.Count, CatalogoPermissoes.Todas.Select(p => p.Id).Distinct().Count());
        Assert.All(CatalogoPermissoes.Todas, p => Assert.True(p.Chave.Length <= CatalogoPermissoes.ChaveMaximo && p.Chave == p.Chave.ToLowerInvariant()));

        var constantes = typeof(Permissoes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!).Order().ToList();
        Assert.Equal(constantes, CatalogoPermissoes.Todas.Select(p => p.Chave).Order().ToList());

        Assert.Equal(CatalogoPermissoes.Todas.Where(p => p.Chave.StartsWith("autorizacao.", StringComparison.Ordinal)).Select(p => p.Chave),
            CatalogoPermissoes.Todas.Where(p => p.ReservadaSistema).Select(p => p.Chave));
        Assert.Equal(
            [Permissoes.UsuariosCriar, Permissoes.UsuariosEditar, Permissoes.UsuariosExcluir, Permissoes.UsuariosFotoAlterar,
             Permissoes.UsuariosSenhaAlterarTerceiros, Permissoes.UsuariosSenhaRecuperacaoAtender],
            CatalogoPermissoes.Todas.Where(p => p.ExigeRegraAlvo).Select(p => p.Chave));
        Assert.Equal([Permissoes.AlmoxarifadoConsultar, Permissoes.AlmoxarifadoGerenciar], CatalogoPermissoes.PorDesignacaoAlmoxarifado);
    }

    [Fact]
    public void MatrizInicial_UsaSoChavesDoCatalogoECargosConhecidos()
    {
        Assert.All(MatrizInicialAutorizacao.Concessoes, c =>
        {
            Assert.Contains(c.Key, TodosOsCargos);
            Assert.All(c.Value, chave => Assert.True(CatalogoPermissoes.PorChave.ContainsKey(chave), chave));
            Assert.Equal(c.Value.Length, c.Value.Distinct().Count());
        });
        Assert.All(MatrizInicialAutorizacao.RegrasAlvo, r =>
        {
            Assert.True(CatalogoPermissoes.PorChave[r.Permissao].ExigeRegraAlvo, r.Permissao);
            Assert.Contains(r.Alvo, TodosOsCargos);
            Assert.Contains(r.Permissao, MatrizInicialAutorizacao.Concessoes[r.Solicitante]);
        });
        Assert.Equal(MatrizInicialAutorizacao.RegrasAlvo.Count, MatrizInicialAutorizacao.RegrasAlvo.Distinct().Count());
        Assert.False(MatrizInicialAutorizacao.Concessoes.ContainsKey("DS"));
        Assert.Equal(["ADM"], MatrizInicialAutorizacao.Protegidos);
    }

    /// <summary>Mesmos módulos por cargo da matriz estática anterior (issue #127), agora com leitura e escrita separadas.</summary>
    [Fact]
    public void MatrizInicial_PreservaOsAcessosAnteriores()
    {
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC"], ComPermissao(Permissoes.UsuariosConsultar));
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC"], ComPermissao(Permissoes.CargosConsultar));
        Assert.Equal(["ADM", "DIR", "DIRA", "TES"], ComPermissao(Permissoes.LancamentosConsultar));
        Assert.Equal(["ADM", "DIR", "DIRA", "TES"], ComPermissao(Permissoes.DashboardFinanceiroConsultar));
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC", "TES"], ComPermissao(Permissoes.EventosCriar));
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC", "TES"], ComPermissao(Permissoes.MembrosConsultar));
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC", "TES"], ComPermissao(Permissoes.DashboardConsultar));
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC"], ComPermissao(Permissoes.FormacaoGerenciar));
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC", "ALM"], ComPermissao(Permissoes.AlmoxarifadoGerenciar));
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC"], ComPermissao(Permissoes.AlmoxarifadoResponsaveisDesignar));
        Assert.Equal([.. TodosOsCargos.Where(r => r != "DS")], ComPermissao(Permissoes.AlmoxarifadoConsultar));
        Assert.Equal(["ADM", "DIR", "SEC"], ComPermissao(Permissoes.UsuariosSenhaAlterarTerceiros));

        // Administração de autorização: só o perfil de sistema, nunca SEC/DIR/DIRA por terem cadastro.
        Assert.All(CatalogoPermissoes.Todas.Where(p => p.ReservadaSistema), p => Assert.Equal(["ADM"], ComPermissao(p.Chave)));

        // Tesoureiro e Secretário de unidade não herdam nada de TES e SEC.
        Assert.Equal([Permissoes.AlmoxarifadoConsultar], MatrizInicialAutorizacao.Concessoes["TESU"]);
        Assert.Equal([Permissoes.AlmoxarifadoConsultar], MatrizInicialAutorizacao.Concessoes["SECU"]);
    }

    /// <summary>Tabela-verdade da antiga hierarquia de cadastro, agora como regras por alvo de cada operação sobre outro usuário.</summary>
    [Fact]
    public void RegrasAlvoIniciais_TabelaCompletaDoCadastro()
    {
        string[] operacoes = [Permissoes.UsuariosCriar, Permissoes.UsuariosEditar, Permissoes.UsuariosExcluir, Permissoes.UsuariosFotoAlterar];
        foreach (var solicitante in TodosOsCargos)
        foreach (var alvo in TodosOsCargos)
        {
            var esperado = solicitante is "ADM" or "DIR" or "DIRA" or "SEC" && alvo switch
            {
                "ADM" => solicitante == "ADM",
                "DIR" or "DIRA" or "SEC" or "TES" => solicitante is "ADM" or "DIR" or "DIRA",
                _ => true,
            };
            foreach (var operacao in operacoes)
            {
                Assert.True(esperado == MatrizInicialAutorizacao.RegrasAlvo.Contains((solicitante, operacao, alvo)),
                    $"{operacao} {solicitante} → {alvo}: esperado {esperado}");
            }
        }
    }

    [Fact]
    public void MigrationAddRbacDinamico_CongelaAMesmaMatriz()
    {
        Assert.Equal(
            MatrizInicialAutorizacao.Concessoes.SelectMany(c => c.Value.Select(p => (c.Key, p))).Order().ToList(),
            AddRbacDinamico.Concessoes.Select(c => (c.Role, c.Permissao)).Order().ToList());
        Assert.Equal(MatrizInicialAutorizacao.RegrasAlvo.Order().ToList(), AddRbacDinamico.RegrasAlvo.Order().ToList());
        Assert.Contains("COLLATE Latin1_General_BIN2", AddRbacDinamico.SqlConcessoes);
        Assert.Contains("('SEC', 'usuarios.senha.alterar-terceiros', 'DS')", AddRbacDinamico.SqlRegrasAlvo);
    }

    /// <summary>
    /// Toda action exige exatamente uma permissão do catálogo na própria action, ou é anônima/autoatendimento por desenho. Nenhum
    /// controller acumula permissão (policies de controller e action são cumulativas: conceder só a leitura precisa liberar o GET).
    /// </summary>
    [Fact]
    public void TodaActionTemUmaPermissaoDoCatalogo_SemPolicyAcumuladaNoController()
    {
        var controllers = typeof(Program).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ControllerBase)) && !t.IsAbstract).ToList();
        var divergencias = new List<string>();
        foreach (var controller in controllers)
        {
            var policiesDoController = controller.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).ToList();
            if (policiesDoController.Any(p => p is not null && p != Policies.Autoatendimento))
            {
                divergencias.Add($"{controller.Name}: policy no controller ({string.Join(", ", policiesDoController)})");
            }

            foreach (var acao in controller.GetMethods().Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>(true).Any()))
            {
                var permissoes = acao.GetCustomAttributes<RequerPermissaoAttribute>(true).ToList();
                var outras = acao.GetCustomAttributes<AuthorizeAttribute>(true).Where(a => a is not RequerPermissaoAttribute).Select(a => a.Policy);
                var anonima = acao.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();
                var autenticada = policiesDoController.Count > 0 || outras.Any();
                if (outras.Any(p => p is not null && p != Policies.Autoatendimento))
                {
                    divergencias.Add($"{controller.Name}.{acao.Name}: policy fora do catálogo");
                }
                else if (permissoes.Count == 1 && !CatalogoPermissoes.PorChave.ContainsKey(permissoes[0].Chave))
                {
                    divergencias.Add($"{controller.Name}.{acao.Name}: chave {permissoes[0].Chave} fora do catálogo");
                }
                else if (permissoes.Count == 0 && !anonima && !autenticada)
                {
                    divergencias.Add($"{controller.Name}.{acao.Name}: sem permissão nem autoatendimento");
                }
            }
        }

        Assert.Empty(divergencias);
    }

    [Fact]
    public async Task PolicyProvider_CriaRequirementSoParaOPrefixoPerm_EDelegaAsDemais()
    {
        var provider = new PermissaoPolicyProvider(Microsoft.Extensions.Options.Options.Create(new AuthorizationOptions()));
        var policy = await provider.GetPolicyAsync("perm:eventos.consultar");
        Assert.Equal("eventos.consultar", Assert.Single(policy!.Requirements.OfType<PermissaoRequirement>()).Chave);
        Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        Assert.Same(policy, await provider.GetPolicyAsync("perm:eventos.consultar"));
        Assert.Null(await provider.GetPolicyAsync("Inexistente"));
        Assert.Equal("perm:eventos.consultar", new RequerPermissaoAttribute(Permissoes.EventosConsultar).Policy);
    }

    [Fact]
    public async Task Handler_ConcedeSoOQueOBancoConcede_EPorRequisicao()
    {
        using var kit = new AutorizacaoTestKit();
        var sec = kit.Usuario("SEC");
        var tes = kit.Usuario("TES");
        var ds = kit.Usuario("DS");
        var adm = kit.Usuario("ADM");

        Assert.Equal(MatrizInicialAutorizacao.Concessoes["SEC"].Order(), (await kit.ConcedidasAsync(sec)).Order());
        Assert.Equal(MatrizInicialAutorizacao.Concessoes["TES"].Order(), (await kit.ConcedidasAsync(tes)).Order());
        Assert.Empty(await kit.ConcedidasAsync(ds));
        Assert.Equal(CatalogoPermissoes.Todas.Select(p => p.Chave).Order(), (await kit.ConcedidasAsync(adm)).Order());
        Assert.Empty(await kit.ConcedidasAsync(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Empty(await kit.ConcedidasAsync(sec, ["chave.inexistente"]));

        // Revogar a concessão, desativar a permissão ou o cargo vale no escopo seguinte (próxima requisição).
        var cargoSec = kit.CargoId("SEC");
        kit.Executar(db => { db.CargoPermissoes.Include(c => c.Permissao).Single(c => c.CargoId == cargoSec && c.Permissao!.Chave == Permissoes.EventosConsultar).Ativo = false; return db.SaveChanges(); });
        kit.Executar(db => { db.Permissoes.Single(p => p.Chave == Permissoes.MembrosConsultar).Ativo = false; return db.SaveChanges(); });
        var depois = await kit.ConcedidasAsync(sec);
        Assert.DoesNotContain(Permissoes.EventosConsultar, depois);
        Assert.DoesNotContain(Permissoes.MembrosConsultar, depois);
        Assert.Contains(Permissoes.UsuariosConsultar, depois);

        kit.Executar(db => { db.Cargos.Single(c => c.Id == cargoSec).Ativo = false; return db.SaveChanges(); });
        Assert.Empty(await kit.ConcedidasAsync(sec));
    }

    [Fact]
    public async Task RegrasPorAlvo_ExigemConcessaoERegraAtivas_ECargoNovoNasceSemNada()
    {
        using var kit = new AutorizacaoTestKit();
        var novo = kit.Executar(db =>
        {
            var cargo = new Almirante.Api.Entities.Cargo { Id = Guid.NewGuid(), Nome = "Auxiliar", Descricao = "Novo", CriadoPor = "Teste", Role = "AUXSEC" };
            db.Cargos.Add(cargo);
            db.SaveChanges();
            return cargo.Id;
        });

        Assert.True(await kit.ComPermissoesAsync(p => p.PodeSobreAlvoAsync("SEC", Permissoes.UsuariosEditar, "DS", default)));
        Assert.False(await kit.ComPermissoesAsync(p => p.PodeSobreAlvoAsync("SEC", Permissoes.UsuariosEditar, "TES", default)));
        Assert.False(await kit.ComPermissoesAsync(p => p.PodeSobreAlvoAsync("SEC", Permissoes.UsuariosEditar, "AUXSEC", default)));
        Assert.False(await kit.ComPermissoesAsync(p => p.PodeSobreAlvoAsync("ADM", Permissoes.UsuariosEditar, "AUXSEC", default)));
        Assert.Empty(await kit.ComPermissoesAsync(p => p.DoCargoAsync(novo, default)));
        Assert.Equal(["DS"], await kit.ComPermissoesAsync(p => p.AlvosPermitidosAsync("SEC", Permissoes.UsuariosSenhaAlterarTerceiros, default)));
        Assert.True(await kit.ComPermissoesAsync(p => p.AlguemPodeSobreAsync("TES", Permissoes.UsuariosSenhaRecuperacaoAtender, default)));
        Assert.False(await kit.ComPermissoesAsync(p => p.AlguemPodeSobreAsync("DIRA", Permissoes.UsuariosSenhaRecuperacaoAtender, default)));

        // Regra sem a concessão não vale: SEC perde usuarios.editar e a regra SEC → DS deixa de valer.
        var cargoSec = kit.CargoId("SEC");
        kit.Executar(db => { db.CargoPermissoes.Include(c => c.Permissao).Single(c => c.CargoId == cargoSec && c.Permissao!.Chave == Permissoes.UsuariosEditar).Ativo = false; return db.SaveChanges(); });
        Assert.False(await kit.ComPermissoesAsync(p => p.PodeSobreAlvoAsync("SEC", Permissoes.UsuariosEditar, "DS", default)));

        // Regra revogada não vale, mesmo com a concessão.
        var cargoDir = kit.CargoId("DIR");
        var cargoTes = kit.CargoId("TES");
        kit.Executar(db => { db.CargoRegrasAlvo.Single(r => r.CargoSolicitanteId == cargoDir && r.CargoAlvoId == cargoTes && r.Permissao!.Chave == Permissoes.UsuariosSenhaAlterarTerceiros).Ativo = false; return db.SaveChanges(); });
        Assert.False(await kit.ComPermissoesAsync(p => p.PodeSobreAlvoAsync("DIR", Permissoes.UsuariosSenhaAlterarTerceiros, "TES", default)));
        Assert.True(await kit.ComPermissoesAsync(p => p.PodeSobreAlvoAsync("DIR", Permissoes.UsuariosSenhaRecuperacaoAtender, "TES", default)));
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
