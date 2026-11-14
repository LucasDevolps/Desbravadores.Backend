using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

/// <summary>
/// RBAC dinâmico pela API (issue #182), com o EF InMemory: criar o perfil AUXSEC, conceder só consultas, atribuí-lo, comprovar o acesso
/// com o mesmo token antes e depois de uma revogação, conflitos de versão, proteção do perfil de sistema, escalada de privilégio e
/// histórico. Concorrência, travas, trigger e grants ficam nos testes de SQL Server.
/// </summary>
public sealed class RbacDinamicoTests
{
    private static object Confirmacao(object corpo, string? senha = null) => Mesclar(corpo, new
    {
        motivo = "Teste da issue #182",
        senhaAtualSolicitante = senha ?? AlmiranteApiFactory.AdminSenha,
    });

    private static Dictionary<string, object?> Mesclar(params object[] partes) =>
        partes.SelectMany(p => JsonSerializer.SerializeToElement(p, JsonSerializerOptions.Web).EnumerateObject())
            .ToDictionary(p => p.Name, p => (object?)p.Value);

    private static async Task<CargoDto> CriarCargoAsync(HttpClient adm, string role)
    {
        var resposta = await adm.PostAsJsonAsync("/api/Cargos", Confirmacao(new { role, nome = $"Cargo {role}", descricao = "Perfil de teste." }));
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<CargoDto>())!;
    }

    private static Task<HttpResponseMessage> ConcederAsync(HttpClient adm, Guid cargoId, long versao, params string[] permissoes) =>
        adm.PutAsJsonAsync($"/api/Cargos/{cargoId}/Permissoes", Confirmacao(new { versao, permissoes }));

    [Fact]
    public async Task AuxiliarDaSecretaria_SoConsultaMembrosEEventos_ERevogacaoValeNaProximaRequisicaoComOMesmoToken()
    {
        using var factory = new AlmiranteApiFactory();
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var cargo = await CriarCargoAsync(adm, " auxsec ");
        Assert.Equal(("AUXSEC", true, false, 1L), (cargo.Role, cargo.Ativo, cargo.SistemaProtegido, cargo.Versao));
        var inicial = await (await adm.GetAsync($"/api/Cargos/{cargo.Id}/Permissoes")).Content.ReadFromJsonAsync<PermissoesCargoDto>();
        Assert.Empty(inicial!.Permissoes);

        var concessao = await ConcederAsync(adm, cargo.Id, 1, Permissoes.MembrosConsultar, Permissoes.EventosConsultar);
        Assert.Equal(HttpStatusCode.OK, concessao.StatusCode);
        Assert.Equal(2, (await concessao.Content.ReadFromJsonAsync<PermissoesCargoDto>())!.Versao);

        var (auxiliar, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "AUXSEC");
        var minhas = (await auxiliar.GetFromJsonAsync<MinhasPermissoesDto>("/api/Me/Permissoes"))!;
        Assert.Equal([Permissoes.EventosConsultar, Permissoes.MembrosConsultar], minhas.Permissoes);
        Assert.Equal(("AUXSEC", cargo.Id), (minhas.Cargo.Role, minhas.Cargo.Id));
        Assert.Empty(minhas.PermissoesPorDesignacao);

        Assert.Equal(HttpStatusCode.OK, (await auxiliar.GetAsync("/api/Membros")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auxiliar.GetAsync("/api/Eventos")).StatusCode);
        var proibidas = new[]
        {
            await auxiliar.PostAsJsonAsync("/api/Eventos", EventosTestKit.Corpo(Guid.NewGuid())),
            await auxiliar.GetAsync("/api/Usuarios"),
            await auxiliar.PutAsJsonAsync($"/api/Usuarios/{Guid.NewGuid()}", new { }),
            await auxiliar.GetAsync("/api/Lancamentos"),
            await auxiliar.GetAsync("/api/Dashboard"),
            await auxiliar.PostAsJsonAsync($"/api/Usuarios/{Guid.NewGuid()}/alterar-senha", new { }),
            await auxiliar.GetAsync("/api/Almoxarifado/Bens"),
            await auxiliar.GetAsync("/api/Permissoes"),
        };
        Assert.All(proibidas, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        // Revogação com o usuário logado: a próxima requisição dele já é recusada, sem novo login.
        Assert.Equal(HttpStatusCode.OK, (await ConcederAsync(adm, cargo.Id, 2, Permissoes.MembrosConsultar)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auxiliar.GetAsync("/api/Eventos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auxiliar.GetAsync("/api/Membros")).StatusCode);
        var depois = (await auxiliar.GetFromJsonAsync<MinhasPermissoesDto>("/api/Me/Permissoes"))!;
        Assert.Equal([Permissoes.MembrosConsultar], depois.Permissoes);
        Assert.True(depois.VersaoAutorizacao > minhas.VersaoAutorizacao);

        // Reconcessão reativa o mesmo vínculo (revogação é lógica, com histórico de quem revogou).
        Assert.Equal(HttpStatusCode.OK, (await ConcederAsync(adm, cargo.Id, 3, Permissoes.MembrosConsultar, Permissoes.EventosConsultar)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auxiliar.GetAsync("/api/Eventos")).StatusCode);
        var vinculos = await TestHelpers.WithDbAsync(factory, db => db.CargoPermissoes.Where(c => c.CargoId == cargo.Id).ToListAsync());
        Assert.Equal(2, vinculos.Count);
        Assert.All(vinculos, v => Assert.True(v.Ativo && v.RevogadaEmUtc is null && v.ConcedidaPorUsuarioId is not null));

        var historico = await TestHelpers.WithDbAsync(factory, db => db.AutorizacaoHistorico.Where(h => h.CargoId == cargo.Id)
            .OrderBy(h => h.RevisaoAutorizacao).ToListAsync());
        Assert.Equal([AutorizacaoHistorico.CargoCriado, AutorizacaoHistorico.ConcessoesSubstituidas, AutorizacaoHistorico.ConcessoesSubstituidas,
            AutorizacaoHistorico.ConcessoesSubstituidas], historico.Select(h => h.Operacao));
        Assert.All(historico, h => Assert.Equal("Teste da issue #182", h.Motivo));
        Assert.DoesNotContain(historico, h => (h.Antes + h.Depois).Contains(AlmiranteApiFactory.AdminSenha, StringComparison.Ordinal));
        Assert.Equal("[\"membros.consultar\"]", historico[2].Depois);
    }

    [Fact]
    public async Task VersaoObsoleta_409_SemSobrescrever()
    {
        using var factory = new AlmiranteApiFactory();
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var cargo = await CriarCargoAsync(adm, "OBSOLETO");
        Assert.Equal(HttpStatusCode.OK, (await ConcederAsync(adm, cargo.Id, 1, Permissoes.MembrosConsultar)).StatusCode);

        var tentativas = new[]
        {
            await ConcederAsync(adm, cargo.Id, 1, Permissoes.EventosConsultar),
            await adm.PutAsJsonAsync($"/api/Cargos/{cargo.Id}", Confirmacao(new { nome = "Outro", descricao = "Outra", versao = 1 })),
            await adm.PostAsJsonAsync($"/api/Cargos/{cargo.Id}/Desativar", Confirmacao(new { versao = 1 })),
            await adm.PutAsJsonAsync($"/api/Cargos/{cargo.Id}/RegrasAlvo", Confirmacao(new { versao = 1, regras = Array.Empty<object>() })),
        };

        Assert.All(tentativas, t => Assert.Equal(HttpStatusCode.Conflict, t.StatusCode));
        var problema = await tentativas[0].Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, problema.GetProperty("versaoAtual").GetInt64());
        var salvo = await TestHelpers.WithDbAsync(factory, db => db.Cargos.AsNoTracking().SingleAsync(c => c.Id == cargo.Id));
        Assert.Equal(("Cargo OBSOLETO", true, 2L), (salvo.Nome, salvo.Ativo, salvo.Versao));
        Assert.Equal(["membros.consultar"], (await adm.GetFromJsonAsync<PermissoesCargoDto>($"/api/Cargos/{cargo.Id}/Permissoes"))!.Permissoes);
    }

    [Fact]
    public async Task CriarEditarDesativarReativar_ComValidacoesESenhaConfirmada()
    {
        using var factory = new AlmiranteApiFactory();
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var invalido = await adm.PostAsJsonAsync("/api/Cargos", new { role = "1x", nome = "", descricao = "", motivo = "", senhaAtualSolicitante = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
        var erros = (await invalido.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.All(new[] { "role", "nome", "descricao", "motivo", "senhaAtualSolicitante" }, campo => Assert.True(erros.TryGetProperty(campo, out _), campo));

        var senhaErrada = await adm.PostAsJsonAsync("/api/Cargos", Confirmacao(new { role = "ERRADA", nome = "x", descricao = "y" }, senha: "Senha-Errada-123"));
        Assert.Equal(HttpStatusCode.BadRequest, senhaErrada.StatusCode);
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Cargos.AnyAsync(c => c.Role == "ERRADA")));

        var cargo = await CriarCargoAsync(adm, "TEMP");
        var duplicada = await adm.PostAsJsonAsync("/api/Cargos", Confirmacao(new { role = "temp", nome = "x", descricao = "y" }));
        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);
        Assert.True((await duplicada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("role", out _));

        var roleNoPut = await adm.PutAsJsonAsync($"/api/Cargos/{cargo.Id}", Confirmacao(new { nome = "Novo", descricao = "Nova", versao = 1, role = "OUTRA" }));
        Assert.Equal(HttpStatusCode.BadRequest, roleNoPut.StatusCode);

        var editado = await adm.PutAsJsonAsync($"/api/Cargos/{cargo.Id}", Confirmacao(new { nome = " Temporário ", descricao = "Nova descrição", versao = 1 }));
        Assert.Equal(HttpStatusCode.OK, editado.StatusCode);
        var dto = (await editado.Content.ReadFromJsonAsync<CargoDto>())!;
        Assert.Equal(("TEMP", "Temporário", 2L), (dto.Role, dto.Nome, dto.Versao));

        // Com usuário ativo no cargo: 409 com a orientação de reatribuir.
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "Usuário temporário", "TEMP");
        var comUsuario = await adm.PostAsJsonAsync($"/api/Cargos/{cargo.Id}/Desativar", Confirmacao(new { versao = 2 }));
        Assert.Equal(HttpStatusCode.Conflict, comUsuario.StatusCode);
        Assert.Equal(1, (await comUsuario.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("usuariosAtivos").GetInt32());

        await TestHelpers.WithDbAsync(factory, async db => { (await db.Usuarios.SingleAsync(u => u.Id == usuario.Id)).Ativo = false; return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.OK, (await adm.PostAsJsonAsync($"/api/Cargos/{cargo.Id}/Desativar", Confirmacao(new { versao = 2 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await adm.PostAsJsonAsync($"/api/Cargos/{cargo.Id}/Desativar", Confirmacao(new { versao = 3 }))).StatusCode);
        var reativado = await adm.PostAsJsonAsync($"/api/Cargos/{cargo.Id}/Reativar", Confirmacao(new { versao = 3 }));
        Assert.Equal(HttpStatusCode.OK, reativado.StatusCode);
        Assert.True((await reativado.Content.ReadFromJsonAsync<CargoDto>())!.Ativo);

        var operacoes = await TestHelpers.WithDbAsync(factory, db => db.AutorizacaoHistorico.Where(h => h.CargoId == cargo.Id)
            .OrderBy(h => h.RevisaoAutorizacao).Select(h => h.Operacao).ToListAsync());
        Assert.Equal([AutorizacaoHistorico.CargoCriado, AutorizacaoHistorico.CargoAlterado, AutorizacaoHistorico.CargoDesativado,
            AutorizacaoHistorico.CargoReativado], operacoes);
    }

    [Fact]
    public async Task PerfilProtegido_NaoEDesativadoNemPerdeAsReservadas_EReservadasNaoVaoParaOutrosCargos()
    {
        using var factory = new AlmiranteApiFactory();
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var cargos = (await adm.GetFromJsonAsync<List<CargoDto>>("/api/Cargos"))!;
        var admin = Assert.Single(cargos, c => c.SistemaProtegido);
        Assert.Equal("ADM", admin.Role);

        Assert.Equal(HttpStatusCode.Conflict,
            (await adm.PostAsJsonAsync($"/api/Cargos/{admin.Id}/Desativar", Confirmacao(new { versao = admin.Versao }))).StatusCode);
        var semReservadas = CatalogoPermissoes.Todas.Where(p => !p.ReservadaSistema).Select(p => p.Chave).ToArray();
        Assert.Equal(HttpStatusCode.Conflict, (await ConcederAsync(adm, admin.Id, admin.Versao, semReservadas)).StatusCode);

        var auxiliar = await CriarCargoAsync(adm, "AUXRES");
        var reservada = await ConcederAsync(adm, auxiliar.Id, 1, Permissoes.AutorizacaoConsultar);
        Assert.Equal(HttpStatusCode.BadRequest, reservada.StatusCode);
        var desconhecida = await ConcederAsync(adm, auxiliar.Id, 1, "financeiro.tudo");
        Assert.Equal(HttpStatusCode.BadRequest, desconhecida.StatusCode);

        var catalogo = (await adm.GetFromJsonAsync<List<PermissaoDto>>("/api/Permissoes"))!;
        Assert.Equal(CatalogoPermissoes.Todas.Count, catalogo.Count);
        Assert.All(catalogo.Where(p => p.ReservadaSistema), p => Assert.StartsWith("autorizacao.", p.Chave));
        Assert.Equal(CatalogoPermissoes.Todas.Select(p => p.Chave).Order(), (await adm.GetFromJsonAsync<MinhasPermissoesDto>("/api/Me/Permissoes"))!.Permissoes);
    }

    [Fact]
    public async Task RegrasPorAlvo_CargoNovoSoEGerenciadoComRegraExplicita_ESemEscaladaSobreOPerfilProtegido()
    {
        using var factory = new AlmiranteApiFactory();
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var auxiliar = await CriarCargoAsync(adm, "AUXREG");
        var (secretario, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "SEC");
        object NovoUsuario() => new
        {
            nome = "Auxiliar", email = $"aux-{Guid.NewGuid():N}@local.dev", senha = "Forte-Senha-9x2k", cargoId = auxiliar.Id,
            cpf = TestHelpers.NovoCpf(), rg = TestHelpers.RgFicticio,
        };

        // Cargo novo não entra na categoria "demais cargos": sem regra, nem a secretaria cadastra nele.
        Assert.Equal(HttpStatusCode.Forbidden, (await secretario.PostAsJsonAsync("/api/Usuarios", NovoUsuario())).StatusCode);

        var sec = (await adm.GetFromJsonAsync<List<CargoDto>>("/api/Cargos"))!.Single(c => c.Role == "SEC");
        var regrasSec = (await adm.GetFromJsonAsync<RegrasAlvoCargoDto>($"/api/Cargos/{sec.Id}/RegrasAlvo"))!;
        Assert.Contains(regrasSec.Regras, r => r.Permissao == Permissoes.UsuariosSenhaAlterarTerceiros && r.CargoAlvoRole == "DS");
        Assert.DoesNotContain(regrasSec.Regras, r => r.CargoAlvoRole is "ADM" or "TES");

        var novas = regrasSec.Regras.Select(r => new { permissao = r.Permissao, cargoAlvoId = r.CargoAlvoId })
            .Append(new { permissao = Permissoes.UsuariosCriar, cargoAlvoId = auxiliar.Id }).ToArray();
        var substituida = await adm.PutAsJsonAsync($"/api/Cargos/{sec.Id}/RegrasAlvo", Confirmacao(new { versao = sec.Versao, regras = novas }));
        Assert.Equal(HttpStatusCode.OK, substituida.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await secretario.PostAsJsonAsync("/api/Usuarios", NovoUsuario())).StatusCode);

        var versao = (await substituida.Content.ReadFromJsonAsync<RegrasAlvoCargoDto>())!.Versao;
        var admin = (await adm.GetFromJsonAsync<List<CargoDto>>("/api/Cargos"))!.Single(c => c.Role == "ADM");
        var semAlvo = await adm.PutAsJsonAsync($"/api/Cargos/{sec.Id}/RegrasAlvo",
            Confirmacao(new { versao, regras = new[] { new { permissao = Permissoes.EventosConsultar, cargoAlvoId = auxiliar.Id } } }));
        Assert.Equal(HttpStatusCode.BadRequest, semAlvo.StatusCode);
        // ADM não tem regra de senha sobre ADM: não pode delegá-la (nem a si mesmo nem a outro cargo).
        var escalada = await adm.PutAsJsonAsync($"/api/Cargos/{sec.Id}/RegrasAlvo",
            Confirmacao(new { versao, regras = new[] { new { permissao = Permissoes.UsuariosSenhaAlterarTerceiros, cargoAlvoId = admin.Id } } }));
        Assert.Equal(HttpStatusCode.Forbidden, escalada.StatusCode);
        Assert.Equal(versao, (await adm.GetFromJsonAsync<RegrasAlvoCargoDto>($"/api/Cargos/{sec.Id}/RegrasAlvo"))!.Versao);
    }

    [Fact]
    public async Task SemAdministracaoDeAutorizacao_GestaoDeCadastrosNaoAlteraPermissoes()
    {
        using var factory = new AlmiranteApiFactory();
        var (diretor, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DIR");
        var cargo = (await diretor.GetFromJsonAsync<List<CargoDto>>("/api/Cargos"))!.Single(c => c.Role == "DIR");

        var tentativas = new[]
        {
            await diretor.PostAsJsonAsync("/api/Cargos", Confirmacao(new { role = "DIRPLUS", nome = "x", descricao = "y" }, TestHelpers.SenhaPadraoTeste)),
            await ConcederAsync(diretor, cargo.Id, cargo.Versao, Permissoes.AutorizacaoConsultar),
            await diretor.GetAsync($"/api/Cargos/{cargo.Id}/Permissoes"),
            await diretor.GetAsync("/api/Permissoes"),
        };

        Assert.All(tentativas, t => Assert.Equal(HttpStatusCode.Forbidden, t.StatusCode));
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Cargos.AnyAsync(c => c.Role == "DIRPLUS")));
        Assert.DoesNotContain(Permissoes.AutorizacaoConsultar, (await diretor.GetFromJsonAsync<MinhasPermissoesDto>("/api/Me/Permissoes"))!.Permissoes);
    }

    [Fact]
    public async Task MinhasPermissoes_IncluiADesignacaoDoAlmoxarifado_ENaoExpoeTerceiros()
    {
        using var factory = new AlmiranteApiFactory();
        var (desbravador, id) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        Assert.Empty((await desbravador.GetFromJsonAsync<MinhasPermissoesDto>("/api/Me/Permissoes"))!.Permissoes);

        await TestHelpers.WithDbAsync(factory, db =>
        {
            db.ResponsaveisAlmoxarifado.Add(new ResponsavelAlmoxarifado { Id = Guid.NewGuid(), MembroId = id, DataInicio = DateOnly.FromDateTime(DateTime.UtcNow) });
            return db.SaveChangesAsync();
        });

        var json = await desbravador.GetStringAsync("/api/Me/Permissoes");
        var minhas = JsonSerializer.Deserialize<MinhasPermissoesDto>(json, JsonSerializerOptions.Web)!;
        Assert.Equal([Permissoes.AlmoxarifadoConsultar, Permissoes.AlmoxarifadoGerenciar], minhas.Permissoes);
        Assert.Equal(minhas.Permissoes, minhas.PermissoesPorDesignacao);
        Assert.Equal("DS", minhas.Cargo.Role);
        Assert.Equal(["cargo", "permissoes", "permissoesPorDesignacao", "versaoAutorizacao"],
            JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(HttpStatusCode.OK, (await desbravador.GetAsync("/api/Almoxarifado/Bens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desbravador.PostAsJsonAsync("/api/Almoxarifado/Responsaveis", new { })).StatusCode);
    }
}
