using Almirante.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

/// <summary>
/// Rollback completo contra SQL Server real: todas as migrations sobem, todas descem em ordem inversa até o banco vazio (cada Down é
/// executado de verdade) e sobem de novo, recriando exatamente o mesmo schema. Os testes de migration específicos cobrem os dados;
/// este garante que nenhum Down antigo ficou quebrado ou incompleto.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class MigrationsReversibilidadeTests
{
    // Nomes de DEFAULT gerados pelo SQL Server (DF__tabela__coluna__hash) mudam a cada criação: só a definição entra na comparação.
    private const string ImpressaoDigitalDoSchema = """
        SELECT CONCAT('C|', t.name COLLATE DATABASE_DEFAULT, '.', c.name COLLATE DATABASE_DEFAULT, ':', ty.name COLLATE DATABASE_DEFAULT, '(', c.max_length, ',', c.precision, ',', c.scale, ')', c.is_nullable,
                      c.is_identity, c.is_computed, '|', ISNULL(dc.definition COLLATE DATABASE_DEFAULT, ''), '|', ISNULL(cc.definition COLLATE DATABASE_DEFAULT, ''), '|', c.collation_name COLLATE DATABASE_DEFAULT)
        FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE t.name COLLATE DATABASE_DEFAULT <> '__EFMigrationsHistory'
        UNION ALL
        SELECT CONCAT('I|', t.name COLLATE DATABASE_DEFAULT, '.', i.name COLLATE DATABASE_DEFAULT, ':', i.type_desc COLLATE DATABASE_DEFAULT, i.is_unique, i.is_primary_key, '|', ISNULL(i.filter_definition COLLATE DATABASE_DEFAULT, ''), '|',
                      (SELECT STRING_AGG(CONCAT(col.name COLLATE DATABASE_DEFAULT, IIF(ic.is_descending_key = 1, ' DESC', ''), IIF(ic.is_included_column = 1, ' INC', '')), ',')
                              WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, col.name COLLATE DATABASE_DEFAULT)
                       FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                       WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id))
        FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
        WHERE i.type > 0 AND t.name COLLATE DATABASE_DEFAULT <> '__EFMigrationsHistory'
        UNION ALL
        SELECT CONCAT('K|', OBJECT_NAME(k.parent_object_id) COLLATE DATABASE_DEFAULT, '.', k.name COLLATE DATABASE_DEFAULT, ':', k.definition COLLATE DATABASE_DEFAULT) FROM sys.check_constraints k
        UNION ALL
        SELECT CONCAT('F|', OBJECT_NAME(f.parent_object_id) COLLATE DATABASE_DEFAULT, '.', f.name COLLATE DATABASE_DEFAULT, '->', OBJECT_NAME(f.referenced_object_id) COLLATE DATABASE_DEFAULT, ':', f.delete_referential_action_desc COLLATE DATABASE_DEFAULT,
                      '|', (SELECT STRING_AGG(CONCAT(COL_NAME(fc.parent_object_id, fc.parent_column_id) COLLATE DATABASE_DEFAULT, '=', COL_NAME(fc.referenced_object_id, fc.referenced_column_id) COLLATE DATABASE_DEFAULT), ',')
                                   WITHIN GROUP (ORDER BY fc.constraint_column_id)
                            FROM sys.foreign_key_columns fc WHERE fc.constraint_object_id = f.object_id))
        FROM sys.foreign_keys f
        UNION ALL
        SELECT CONCAT('T|', OBJECT_NAME(tr.parent_id) COLLATE DATABASE_DEFAULT, '.', tr.name COLLATE DATABASE_DEFAULT, ':', tr.is_disabled, '|', CONVERT(varchar(64), HASHBYTES('SHA2_256', m.definition), 2))
        FROM sys.triggers tr JOIN sys.sql_modules m ON m.object_id = tr.object_id WHERE tr.parent_class = 1
        UNION ALL
        SELECT CONCAT('V|', s.name COLLATE DATABASE_DEFAULT, '.', v.name COLLATE DATABASE_DEFAULT, '|', CONVERT(varchar(64), HASHBYTES('SHA2_256', m.definition), 2))
        FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id JOIN sys.sql_modules m ON m.object_id = v.object_id
        UNION ALL
        SELECT CONCAT('R|', p.name COLLATE DATABASE_DEFAULT, ':', p.type_desc COLLATE DATABASE_DEFAULT) FROM sys.database_principals p WHERE p.type = 'R' AND p.is_fixed_role = 0 AND p.principal_id > 4
        """;

    private const string ObjetosDaAplicacao = """
        SELECT (SELECT COUNT(*) FROM sys.tables WHERE name <> '__EFMigrationsHistory')
             + (SELECT COUNT(*) FROM sys.views)
             + (SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 1)
             + (SELECT COUNT(*) FROM sys.procedures)
             + (SELECT COUNT(*) FROM sys.objects WHERE type IN ('FN', 'IF', 'TF'))
             + (SELECT COUNT(*) FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383)
        """;

    private static AlmiranteDbContext Contexto(string connectionString) =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(connectionString, sql => sql.CommandTimeout(300)).Options);

    private static async Task<List<string>> ImpressaoDigitalAsync(string connectionString)
    {
        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync();
        await using var comando = new SqlCommand(ImpressaoDigitalDoSchema, conexao) { CommandTimeout = 120 };
        await using var leitor = await comando.ExecuteReaderAsync();
        var linhas = new List<string>();
        while (await leitor.ReadAsync()) linhas.Add(leitor.GetString(0));
        linhas.Sort(StringComparer.Ordinal);
        return linhas;
    }

    private static async Task<int> EscalarAsync(string connectionString, string sql)
    {
        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync();
        await using var comando = new SqlCommand(sql, conexao);
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task TodasAsMigrations_DescemAteOBancoVazio_ESobemDeNovoComOMesmoSchema()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        await using var db = Contexto(cs);
        var migrator = db.GetService<IMigrator>();
        var todas = db.Database.GetMigrations().ToList();

        await migrator.MigrateAsync();
        Assert.Equal(todas, await db.Database.GetAppliedMigrationsAsync());
        var schemaOriginal = await ImpressaoDigitalAsync(cs);
        Assert.Contains(schemaOriginal, l => l.StartsWith("T|Usuarios.TR_Usuarios_Historico:", StringComparison.Ordinal));

        await migrator.MigrateAsync(Migration.InitialDatabase);

        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await EscalarAsync(cs, ObjetosDaAplicacao));

        await migrator.MigrateAsync();

        Assert.Equal(todas, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(schemaOriginal, await ImpressaoDigitalAsync(cs));
    }

    // Regressão: UnifyLancamentosFlow.Down apagava o trigger e não recriava o anterior, então um banco revertido para antes da
    // unificação aceitava exclusão lógica sem contexto e sem cópia em lancamentos_deletados.
    [Fact]
    public async Task RollbackParaAntesDaUnificacao_ExclusaoLogicaContinuaAuditada()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        await using var db = Contexto(cs);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync("20260917000000_AddAuthenticationSessions");

        var (cargo, membro, lancamento) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var conexao = new SqlConnection(cs);
        await conexao.OpenAsync();
        await using (var semear = new SqlCommand("""
            INSERT INTO dbo.Cargos (Id, Nome, Descricao, Ativo, CriadoPor, CriadoEm, Role) VALUES (@c, N'Desbravador', N'Membro', 1, N'Teste', SYSUTCDATETIME(), N'DS');
            INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao)
            VALUES (@m, N'Membro', N'm@local.dev', N'M@LOCAL.DEV', N'hash', @c, SYSUTCDATETIME());
            INSERT INTO dbo.Lancamentos (Id, MembroId, MembroNome, Tipo, Descricao, Categoria, TipoFluxo, Valor, Moeda, Vencimento, Status, DataCriacao, Ativo)
            VALUES (@l, @m, N'Membro', N'Mensalidade', N'Outubro', N'Clube', N'Entrada', 50.00, N'BRL', '2026-11-10', N'Pendente', SYSUTCDATETIME(), 1);
            """, conexao))
        {
            semear.Parameters.AddWithValue("@c", cargo);
            semear.Parameters.AddWithValue("@m", membro);
            semear.Parameters.AddWithValue("@l", lancamento);
            await semear.ExecuteNonQueryAsync();
        }

        await using (var semContexto = new SqlCommand("UPDATE dbo.Lancamentos SET Ativo = 0 WHERE Id = @l", conexao))
        {
            semContexto.Parameters.AddWithValue("@l", lancamento);
            var erro = await Assert.ThrowsAsync<SqlException>(() => semContexto.ExecuteNonQueryAsync());
            Assert.Equal(51000, erro.Number);
        }

        await using (var excluir = new SqlCommand("""
            EXEC sys.sp_set_session_context N'UsuarioResponsavelId', @m;
            EXEC sys.sp_set_session_context N'IpResponsavelExclusao', '203.0.113.7';
            EXEC sys.sp_set_session_context N'MotivoExclusao', N'Lançado em duplicidade';
            UPDATE dbo.Lancamentos SET Ativo = 0 WHERE Id = @l;
            SELECT CONCAT(UsuarioResponsavelId, '|', IpResponsavel, '|', Motivo, '|', MembroNome, '|', Tipo, '|', Moeda, '|', TipoFluxo, '|', Valor)
            FROM dbo.lancamentos_deletados WHERE LancamentoId = @l;
            """, conexao))
        {
            excluir.Parameters.AddWithValue("@m", membro);
            excluir.Parameters.AddWithValue("@l", lancamento);
            Assert.Equal($"{membro.ToString().ToUpperInvariant()}|203.0.113.7|Lançado em duplicidade|Membro|Mensalidade|BRL|Entrada|50.00",
                (string?)await excluir.ExecuteScalarAsync());
        }
    }
}
