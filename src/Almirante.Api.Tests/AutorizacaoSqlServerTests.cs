using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Data.Migrations;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Almirante.Api.Tests.SenhasTestKit;

namespace Almirante.Api.Tests;

/// <summary>
/// RBAC dinâmico (issue #182) contra SQL Server real: conversão dos acessos atuais pela migration num banco preenchido (Ids preservados,
/// cargo customizado sem concessões, Down/Up), histórico imutável, revogação que vale na próxima requisição com o mesmo token, regra por
/// alvo relida na transação das operações de senha e segunda inicialização sem ressuscitar concessões revogadas.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AutorizacaoSqlServerTests
{
    private const string MigrationAnterior = "20261106134004_AddNotificacaoDebitoAtrasado";
    private const string MigrationEmTeste = "20261114125930_AddRbacDinamico";

    private static AlmiranteDbContext Contexto(string connectionString) =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(connectionString).Options);

    private static async Task<int> EscalarAsync(string cs, string sql)
    {
        await using var conexao = new SqlConnection(cs);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private static object Confirmacao(object corpo) => new Dictionary<string, object?>(
        System.Text.Json.JsonSerializer.SerializeToElement(corpo, System.Text.Json.JsonSerializerOptions.Web).EnumerateObject()
            .Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)))
    {
        ["motivo"] = "Teste SQL da issue #182",
        ["senhaAtualSolicitante"] = AlmiranteApiFactory.AdminSenha,
    };

    [Fact]
    public async Task Migration_ConverteOsAcessosAtuaisPreservandoIds_SemConcederACargoCustomizado_EDownUpReaplica()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        var cargos = MatrizInicialAutorizacao.CargosConhecidos
            .Select(role => new Cargo { Id = Guid.NewGuid(), Nome = $"Cargo {role}", Descricao = "Legado.", CriadoPor = "Teste", Role = role })
            .Append(new Cargo { Id = Guid.NewGuid(), Nome = "Customizado", Descricao = "Criado antes da #182.", CriadoPor = "Teste", Role = "CUSTOM" })
            .ToArray();
        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            await TestHelpers.InserirCargosLegadosAsync(db, cargos);
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
        }

        async Task ConferirAsync()
        {
            await using var db = Contexto(cs);
            var salvos = await db.Cargos.AsNoTracking().ToListAsync();
            Assert.Equal(cargos.Select(c => c.Id).Order(), salvos.Select(c => c.Id).Order());
            Assert.Equal(["ADM"], salvos.Where(c => c.SistemaProtegido).Select(c => c.Role));
            Assert.All(salvos, c => Assert.Equal(1, c.Versao));
            Assert.Equal(CatalogoPermissoes.Todas.Count, await db.Permissoes.CountAsync(p => p.Ativo));
            Assert.Equal(1, await db.AutorizacaoEstado.CountAsync());

            var concessoes = await db.CargoPermissoes.AsNoTracking().Where(c => c.Ativo)
                .Select(c => new { c.Cargo!.Role, c.Permissao!.Chave }).ToListAsync();
            Assert.Equal(MatrizInicialAutorizacao.Concessoes.SelectMany(c => c.Value.Select(p => (c.Key, p))).Order(),
                concessoes.Select(c => (c.Role, c.Chave)).Order());
            var regras = await db.CargoRegrasAlvo.AsNoTracking().Where(r => r.Ativo)
                .Select(r => new { S = r.CargoSolicitante!.Role, r.Permissao!.Chave, A = r.CargoAlvo!.Role }).ToListAsync();
            Assert.Equal(MatrizInicialAutorizacao.RegrasAlvo.Order(), regras.Select(r => (r.S, r.Chave, r.A)).Order());
            Assert.DoesNotContain(concessoes, c => c.Role == "CUSTOM");
            Assert.DoesNotContain(regras, r => r.S == "CUSTOM" || r.A == "CUSTOM");
        }

        await ConferirAsync();
        Assert.Equal(1, await EscalarAsync(cs, $"SELECT COUNT(*) FROM sys.triggers WHERE name = '{AlmiranteDbContext.TriggerAutorizacaoHistoricoImutavel}'"));
        Assert.Equal(0, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id JOIN sys.tables tb ON tb.object_id = c.object_id
            WHERE t.name IN ('nvarchar', 'nchar', 'ntext')
              AND tb.name IN ('Permissoes', 'CargoPermissoes', 'CargoRegrasAlvo', 'AutorizacaoEstado', 'Autorizacao_historico')
            """));

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            Assert.Equal(0, await EscalarAsync(cs, """
                SELECT COUNT(*) FROM sys.tables WHERE name IN ('Permissoes', 'CargoPermissoes', 'CargoRegrasAlvo', 'AutorizacaoEstado', 'Autorizacao_historico')
                """));
            Assert.Equal(0, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Cargos') AND name IN ('SistemaProtegido', 'Versao')"));
            await db.GetService<IMigrator>().MigrateAsync();
        }

        await ConferirAsync();
    }

    [Fact]
    public async Task BancoNovo_SeederSemeiaAMatriz_HistoricoEImutavel_ESegundaInicializacaoNaoRessuscitaRevogacao()
    {
        using var factory = new SqlServerApiFactory();
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var sec = (await adm.GetFromJsonAsync<List<CargoDto>>("/api/Cargos"))!.Single(c => c.Role == "SEC");
        var permissoes = (await adm.GetFromJsonAsync<PermissoesCargoDto>($"/api/Cargos/{sec.Id}/Permissoes"))!;
        Assert.Equal(MatrizInicialAutorizacao.Concessoes["SEC"].Order(), permissoes.Permissoes);

        var revogada = await adm.PutAsJsonAsync($"/api/Cargos/{sec.Id}/Permissoes",
            Confirmacao(new { versao = permissoes.Versao, permissoes = permissoes.Permissoes.Where(p => p != Permissoes.EventosExcluir) }));
        Assert.Equal(HttpStatusCode.OK, revogada.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            await DbSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>(),
                scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(), scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>());
        }

        Assert.DoesNotContain(Permissoes.EventosExcluir, (await adm.GetFromJsonAsync<PermissoesCargoDto>($"/api/Cargos/{sec.Id}/Permissoes"))!.Permissoes);
        var historicoId = await TestHelpers.WithDbAsync(factory, db => db.AutorizacaoHistorico.Where(h => h.CargoId == sec.Id).Select(h => h.Id).SingleAsync());
        var erro = await Assert.ThrowsAsync<SqlException>(() => EscalarAsync(factory.ConnectionString,
            $"UPDATE dbo.Autorizacao_historico SET Motivo = 'adulterado' WHERE Id = '{historicoId}'"));
        Assert.Equal(50182, erro.Number);
        await Assert.ThrowsAsync<SqlException>(() => EscalarAsync(factory.ConnectionString, $"DELETE dbo.Autorizacao_historico WHERE Id = '{historicoId}'"));
    }

    [Fact]
    public async Task Revogacao_ValeNaProximaRequisicaoComOMesmoToken_InclusiveNasOperacoesDeSenha()
    {
        using var factory = new SqlServerApiFactory(SemLimites());
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var secretario = await ContaAsync(factory, "SEC", NovaSenha());
        var desbravador = await ContaAsync(factory, "DS");
        using var sessao = await LoginAsync(factory, secretario);
        Assert.Equal(HttpStatusCode.OK, (await sessao.Client.GetAsync("/api/Eventos")).StatusCode);

        var sec = (await adm.GetFromJsonAsync<List<CargoDto>>("/api/Cargos"))!.Single(c => c.Role == "SEC");
        var regras = (await adm.GetFromJsonAsync<RegrasAlvoCargoDto>($"/api/Cargos/{sec.Id}/RegrasAlvo"))!;
        var semSenhaDs = regras.Regras.Where(r => r.Permissao != Permissoes.UsuariosSenhaAlterarTerceiros)
            .Select(r => new { permissao = r.Permissao, cargoAlvoId = r.CargoAlvoId }).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await adm.PutAsJsonAsync($"/api/Cargos/{sec.Id}/RegrasAlvo",
            Confirmacao(new { versao = regras.Versao, regras = semSenhaDs }))).StatusCode);

        var antes = await EstadoAsync(factory, desbravador.Id);
        using var resposta = await AlterarDeUsuarioAsync(sessao.Client, desbravador.Id, secretario.Senha, NovaSenha());
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal(antes, await EstadoAsync(factory, desbravador.Id));

        var permissoes = (await adm.GetFromJsonAsync<PermissoesCargoDto>($"/api/Cargos/{sec.Id}/Permissoes"))!;
        Assert.Equal(HttpStatusCode.OK, (await adm.PutAsJsonAsync($"/api/Cargos/{sec.Id}/Permissoes",
            Confirmacao(new { versao = permissoes.Versao, permissoes = permissoes.Permissoes.Where(p => p != Permissoes.EventosConsultar) }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sessao.Client.GetAsync("/api/Eventos")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sessao.Client.GetAsync("/api/Usuarios")).StatusCode);

        var revisoes = await TestHelpers.WithDbAsync(factory, db => db.AutorizacaoHistorico.Where(h => h.CargoId == sec.Id)
            .OrderBy(h => h.OcorridoEmUtc).Select(h => h.RevisaoAutorizacao).ToListAsync());
        Assert.Equal(2, revisoes.Count);
        Assert.True(revisoes[1] > revisoes[0]);
        Assert.Equal(revisoes[1], await TestHelpers.WithDbAsync(factory, db => db.AutorizacaoEstado.Select(e => e.Revisao).SingleAsync()));
    }

    [Fact]
    public async Task UltimoAdministrador_NaoEExcluidoNemReclassificado()
    {
        using var factory = new SqlServerApiFactory();
        using var adm = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var outroAdmin = await TestHelpers.AddUsuarioAsync(factory, "Outro administrador", "ADM");
        var ds = await TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await adm.DeleteAsync($"/api/Usuarios/{outroAdmin.Id}")).StatusCode);

        // O administrador logado é agora o único usuário ativo do perfil protegido: não pode ser reclassificado.
        var adminId = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Email == AlmiranteApiFactory.AdminEmail).Select(u => u.Id).SingleAsync());
        var reclassificar = await adm.PutAsJsonAsync($"/api/Usuarios/{adminId}",
            new { nome = "Administrador", email = AlmiranteApiFactory.AdminEmail, cargoId = ds });
        Assert.Equal(HttpStatusCode.Conflict, reclassificar.StatusCode);
        Assert.Equal("Último administrador.", (await reclassificar.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
        Assert.True(await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AnyAsync(u => u.Id == adminId && u.Ativo && u.Cargo!.Role == "ADM")));
    }
}
