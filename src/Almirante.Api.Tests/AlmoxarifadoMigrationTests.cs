using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

/// <summary>
/// Migration AddAlmoxarifadoPatrimonio contra SQL Server real: cria o cargo ALM só numa instalação semeada que não o tenha, nunca altera um
/// ALM existente, deixa banco novo para o seeder, cria tabelas e triggers sem nvarchar e o Down remove tudo menos o cargo.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AlmoxarifadoMigrationTests
{
    private const string MigrationAnterior = "20261022123632_AddFormacaoClassesEspecialidades";
    private const string MigrationEmTeste = "20261023182429_AddAlmoxarifadoPatrimonio";

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

    private static Cargo Cargo(string role, bool ativo = true) =>
        new() { Id = Guid.NewGuid(), Nome = $"Cargo {role}", Descricao = "Descrição original.", CriadoPor = "Teste", Role = role, Ativo = ativo };

    [Fact]
    public async Task Up_CriaCargoAlmoxarifeSoQuandoFaltaNumaInstalacaoSemeada_EDownRemoveTabelasETriggersMantendoOCargo()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            db.Cargos.AddRange(Cargo("ADM"), Cargo("DS"));
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
        }

        await using (var db = Contexto(cs))
        {
            var almoxarife = await db.Cargos.SingleAsync(c => c.Role == "ALM");
            Assert.Equal(("Almoxarife", true, "Sistema"), (almoxarife.Nome, almoxarife.Ativo, almoxarife.CriadoPor));
            Assert.Equal(10, await EscalarAsync(cs, """
                SELECT COUNT(*) FROM sys.tables WHERE name IN ('patrimonio_bens', 'patrimonio_emprestimos', 'patrimonio_manutencoes', 'patrimonio_inventarios',
                  'patrimonio_inventario_itens', 'almoxarifado_desejos', 'almoxarifado_atendimentos', 'almoxarifado_atendimentos_bens',
                  'almoxarifado_responsaveis', 'almoxarifado_historico')
                """));
            Assert.Equal(11, await EscalarAsync(cs, """
                SELECT COUNT(*) FROM sys.triggers WHERE name LIKE 'TR[_]patrimonio[_]%' OR name LIKE 'TR[_]almoxarifado[_]%'
                """));
            Assert.Equal(0, await EscalarAsync(cs, """
                SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id JOIN sys.tables tb ON tb.object_id = c.object_id
                WHERE t.name IN ('nvarchar', 'nchar', 'ntext') AND (tb.name LIKE 'patrimonio[_]%' OR tb.name LIKE 'almoxarifado[_]%')
                """));
            Assert.Equal(0, await EscalarAsync(cs, """
                SELECT COUNT(*) FROM sys.sql_modules m JOIN sys.triggers t ON t.object_id = m.object_id WHERE m.definition LIKE '%nvarchar%'
                """));
            Assert.Equal(1, await EscalarAsync(cs, """
                SELECT COUNT(*) FROM sys.computed_columns WHERE object_id = OBJECT_ID('dbo.patrimonio_bens') AND name = 'Tombo' AND is_persisted = 1
                """));
            foreach (var indice in new[]
            {
                "UX_patrimonio_emprestimos_bem_aberto", "UX_patrimonio_manutencoes_bem_aberta", "UX_almoxarifado_atendimentos_bens_bem_ativo",
                "UX_almoxarifado_responsaveis_membro_vigente",
            })
            {
                Assert.Equal(1, await EscalarAsync(cs, $"SELECT COUNT(*) FROM sys.indexes WHERE name = '{indice}' AND is_unique = 1 AND has_filter = 1"));
            }

            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            Assert.Equal(0, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'patrimonio[_]%' OR name LIKE 'almoxarifado[_]%'"));
            Assert.Equal(0, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.triggers WHERE name LIKE 'TR[_]patrimonio[_]%' OR name LIKE 'TR[_]almoxarifado[_]%'"));
            Assert.Equal(1, await db.Cargos.CountAsync(c => c.Role == "ALM"));

            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(1, await db.Cargos.CountAsync(c => c.Role == "ALM"));
        }
    }

    [Fact]
    public async Task Up_NaoAlteraAlmoxarifeExistente_ENaoSemeiaBancoSemCargos()
    {
        using var existente = new SqlServerApiFactory();
        await using (var db = Contexto(existente.ConnectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            var inativo = Cargo("ALM", ativo: false);
            db.Cargos.AddRange(Cargo("ADM"), inativo);
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync();
            var alm = await db.Cargos.AsNoTracking().SingleAsync(c => c.Role == "ALM");
            Assert.Equal((inativo.Id, false, "Cargo ALM", "Descrição original."), (alm.Id, alm.Ativo, alm.Nome, alm.Descricao));
        }

        using var vazio = new SqlServerApiFactory();
        await using (var db = Contexto(vazio.ConnectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(0, await db.Cargos.CountAsync());
        }
    }
}
