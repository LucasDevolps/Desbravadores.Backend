using Almirante.Api.Data;
using Almirante.Api.Data.Migrations;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

/// <summary>
/// Migration ConverterColunasLegadasParaVarchar contra SQL Server real: nenhuma coluna nem trigger da aplicação fica em nvarchar,
/// acentos do português sobrevivem, dado antigo fora da página 1252 não derruba o deploy (vai para a tabela de backup) e o Down
/// devolve o original.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class VarcharColunasMigrationTests
{
    private const string MigrationAnterior = "20261021012556_AddNotificacoes";
    private const string MigrationEmTeste = "20261021201658_ConverterColunasLegadasParaVarchar";
    private const string Backup = ConverterColunasLegadasParaVarchar.TabelaBackup;

    private const string ColunasUnicode = """
        SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id JOIN sys.tables tb ON tb.object_id = c.object_id
        WHERE t.name IN ('nvarchar', 'nchar', 'ntext') AND tb.name NOT IN ('__EFMigrationsHistory', '_varchar_conversao_backup')
        """;

    private const string TriggersComNvarchar = """
        SELECT COUNT(*) FROM sys.sql_modules m JOIN sys.triggers t ON t.object_id = m.object_id WHERE m.definition LIKE '%nvarchar%'
        """;

    private static AlmiranteDbContext Contexto(string connectionString) =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(connectionString).Options);

    private static async Task<object?> EscalarAsync(string cs, string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(cs);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return await comando.ExecuteScalarAsync();
    }

    private static async Task<(Guid Acentuado, Guid ComEmoji, Guid Lancamento)> PrepararAsync(string cs, AlmiranteDbContext db,
        string nomeForaDaPagina1252 = "José 😀 中")
    {
        await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Jovem membro do clube.", CriadoPor = "Teste", Role = "DS" };
        db.Cargos.Add(cargo);
        await db.SaveChangesAsync();

        var (acentuado, comEmoji, lancamento) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await EscalarAsync(cs, """
            INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Ativo)
            VALUES (@a, N'Conceição Ação Ñandú', 'acentos@local.dev', 'ACENTOS@LOCAL.DEV', N'hash', @cargo, SYSUTCDATETIME(), 0, 0, 1),
                   (@e, @nome, 'emoji@local.dev', 'EMOJI@LOCAL.DEV', N'hash', @cargo, SYSUTCDATETIME(), 0, 0, 1);
            INSERT INTO dbo.Lancamentos (Id, MembroId, Finalidade, Descricao, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, Ativo)
            VALUES (@l, @a, N'Doação', N'Contribuição de outubro', 1, 50.00, '2026-11-10', 0, 0, SYSUTCDATETIME(), 1);
            """, ("@a", acentuado), ("@e", comEmoji), ("@l", lancamento), ("@cargo", cargo.Id), ("@nome", nomeForaDaPagina1252));
        return (acentuado, comEmoji, lancamento);
    }

    [Fact]
    public async Task Up_ConverteTudoParaVarchar_PreservaAcentos_GuardaOQueNaoCabe_ETriggersContinuamAuditando_DownRestaura()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        Guid acentuado, comEmoji, lancamento;
        await using (var db = Contexto(cs))
        {
            (acentuado, comEmoji, lancamento) = await PrepararAsync(cs, db);
            Assert.True((int)(await EscalarAsync(cs, ColunasUnicode))! > 0);
            Assert.True((int)(await EscalarAsync(cs, TriggersComNvarchar))! > 0);

            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
        }

        Assert.Equal(0, await EscalarAsync(cs, ColunasUnicode));
        Assert.Equal(0, await EscalarAsync(cs, TriggersComNvarchar));
        Assert.Equal(3, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.check_constraints WHERE name IN
                ('CK_lancamentos_deletados_Motivo', 'CK_eventos_operacoes_RespostaJson', 'CK_historico_eventos_ParticipantesJson')
            """));
        Assert.Equal(4, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.indexes WHERE name IN ('IX_Lancamentos_Finalidade', 'IX_Cargos_Role',
                'IX_LancamentosOperacoes_IdempotencyKey', 'IX_eventos_operacoes_UsuarioId_IdempotencyKey')
            """));

        // Acentos da página 1252 passam intactos e não vão para o backup.
        Assert.Equal("Conceição Ação Ñandú", await EscalarAsync(cs, "SELECT Nome FROM dbo.Usuarios WHERE Id = @id", ("@id", acentuado)));
        Assert.Equal("Doação|Contribuição de outubro",
            await EscalarAsync(cs, "SELECT Finalidade + '|' + Descricao FROM dbo.Lancamentos WHERE Id = @id", ("@id", lancamento)));

        // O que não cabe não derruba a migration: o original fica guardado (UTF-16) e só ele.
        Assert.Equal(1, await EscalarAsync(cs, $"SELECT COUNT(*) FROM dbo.[{Backup}]"));
        Assert.Equal("José 😀 中", await EscalarAsync(cs, $"""
            SELECT CAST(ValorOriginal AS nvarchar(max)) FROM dbo.[{Backup}]
            WHERE Tabela = 'Usuarios' AND Coluna = 'Nome' AND RegistroId = @id
            """, ("@id", comEmoji)));

        // Triggers recriados continuam gravando a auditoria, agora com contexto varchar.
        await EscalarAsync(cs, """
            EXEC sys.sp_set_session_context @key = N'UsuarioResponsavelId', @value = @usuario;
            EXEC sys.sp_set_session_context @key = N'IpResponsavelExclusao', @value = '127.0.0.1';
            EXEC sys.sp_set_session_context @key = N'MotivoExclusao', @value = 'Cobrança duplicada — correção';
            UPDATE dbo.Lancamentos SET Ativo = 0 WHERE Id = @lancamento;
            UPDATE dbo.Usuarios SET Nome = 'Conceição Revisada' WHERE Id = @usuario;
            """, ("@usuario", acentuado), ("@lancamento", lancamento));
        Assert.Equal("Cobrança duplicada — correção",
            await EscalarAsync(cs, "SELECT Motivo FROM dbo.lancamentos_deletados WHERE LancamentoId = @id", ("@id", lancamento)));
        Assert.Equal("Conceição Ação Ñandú|acentos@local.dev", await EscalarAsync(cs,
            "SELECT Nome + '|' + UsuarioResponsavelLogin FROM dbo._usuarios_hist WHERE UsuarioId = @id", ("@id", acentuado)));

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal("nvarchar", await EscalarAsync(cs, """
            SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.Usuarios') AND c.name = 'Nome'
            """));
        Assert.Equal("José 😀 中", await EscalarAsync(cs, "SELECT Nome FROM dbo.Usuarios WHERE Id = @id", ("@id", comEmoji)));
        Assert.Equal(DBNull.Value, await EscalarAsync(cs, $"SELECT OBJECT_ID('dbo.[{Backup}]')"));
        Assert.Equal(3, await EscalarAsync(cs, TriggersComNvarchar));
        Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = @id", ("@id", acentuado)));
    }

    [Fact]
    public async Task Up_SemDadoForaDaPagina1252_NaoCriaTabelaDeBackup()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        await using var db = Contexto(cs);
        await PrepararAsync(cs, db, nomeForaDaPagina1252: "José Silva");

        await db.GetService<IMigrator>().MigrateAsync();

        Assert.Equal(0, await EscalarAsync(cs, ColunasUnicode));
        Assert.Equal(DBNull.Value, await EscalarAsync(cs, $"SELECT OBJECT_ID('dbo.[{Backup}]')"));
    }
}
