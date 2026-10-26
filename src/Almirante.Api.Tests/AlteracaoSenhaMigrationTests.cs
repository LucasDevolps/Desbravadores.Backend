using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

/// <summary>Migration AddAlteracaoERecuperacaoSenha (issue #159) aplicada sobre dados existentes, com restrições, índices, trigger e Down.</summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AlteracaoSenhaMigrationTests
{
    private const string MigrationAnterior = "20261022123632_AddFormacaoClassesEspecialidades";
    private const string MigrationEmTeste = "20261026183106_AddAlteracaoERecuperacaoSenha";

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

    private static async Task<int> ErroSqlAsync(string cs, string sql, params (string Nome, object Valor)[] parametros) =>
        (await Assert.ThrowsAsync<SqlException>(() => EscalarAsync(cs, sql, parametros))).Number;

    [Fact]
    public async Task Up_CriaTabelasVaziasComRestricoesIndicesETrigger_PreservaDados_EDownRemove()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        var responsavel = Guid.NewGuid();
        var afetado = Guid.NewGuid();
        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Membro.", CriadoPor = "Teste", Role = "DS" };
            db.Cargos.Add(cargo);
            await db.SaveChangesAsync();
            foreach (var id in new[] { responsavel, afetado })
            {
                await EscalarAsync(cs, """
                    INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Ativo)
                    VALUES (@id, 'Membro', @email, UPPER(@email), 'hash-existente', @cargo, SYSUTCDATETIME(), 7, 0, 1);
                    INSERT INTO dbo.notificacoes (Id, DestinatarioId, Tipo, Titulo, Mensagem, CriadaEmUtc, ChaveOrigem, AvisoTentativas)
                    VALUES (NEWID(), @id, 1, 't', 'm', SYSUTCDATETIME(), CONCAT('lancamento:', NEWID()), 0);
                    """, ("@id", id), ("@email", $"{id:N}@local.dev"), ("@cargo", cargo.Id));
            }

            await db.GetService<IMigrator>().MigrateAsync(MigrationEmTeste);
        }

        Assert.Equal(0, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.Usuarios_alteracoes_senha"));
        Assert.Equal(0, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.Usuarios_recuperacoes_senha"));
        Assert.Equal(2, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.notificacoes WHERE Tipo = 1"));
        Assert.Equal(2, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.Usuarios WHERE SenhaHash = 'hash-existente' AND SecurityVersion = 7"));

        // Nenhum cascade: o histórico nunca some junto com o usuário.
        Assert.Equal(5, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id IN (OBJECT_ID('dbo.Usuarios_alteracoes_senha'), OBJECT_ID('dbo.Usuarios_recuperacoes_senha'))
              AND delete_referential_action = 0 AND update_referential_action = 0
            """));
        Assert.Equal(0, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id IN (OBJECT_ID('dbo.Usuarios_alteracoes_senha'), OBJECT_ID('dbo.Usuarios_recuperacoes_senha'))
              AND delete_referential_action <> 0
            """));
        foreach (var (indice, unico, filtro) in new[]
        {
            ("IX_Usuarios_alteracoes_senha_afetado_data", 0, 0), ("IX_Usuarios_alteracoes_senha_responsavel_data", 0, 0),
            ("UX_Usuarios_alteracoes_senha_recuperacao", 1, 1), ("UX_Usuarios_recuperacoes_senha_aberta", 1, 1),
            ("UX_Usuarios_recuperacoes_senha_token", 1, 1), ("IX_Usuarios_recuperacoes_senha_abertas", 0, 1),
        })
        {
            Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = @n AND is_unique = @u AND has_filter = @f",
                ("@n", indice), ("@u", unico), ("@f", filtro)));
        }
        Assert.Equal(0, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id IN (OBJECT_ID('dbo.Usuarios_alteracoes_senha'), OBJECT_ID('dbo.Usuarios_recuperacoes_senha'))
              AND t.name IN ('nvarchar', 'nchar', 'ntext')
            """));
        // A auditoria não tem onde guardar senha, hash, token ou cópia de dados pessoais.
        Assert.Equal("AlteradaEmUtc,Id,IpResponsavel,SolicitacaoRecuperacaoId,TipoAlteracao,TraceId,UsuarioAfetadoId,UsuarioResponsavelId",
            await EscalarAsync(cs, "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Usuarios_alteracoes_senha')"));
        Assert.Equal(1, await EscalarAsync(cs, $"SELECT COUNT(*) FROM sys.triggers WHERE name = '{AlmiranteDbContext.TriggerAlteracoesSenhaImutavel}' AND is_instead_of_trigger = 1"));

        // CK_notificacoes_Tipo aceita SenhaAlterada (3) e continua recusando valores desconhecidos.
        await EscalarAsync(cs, "INSERT INTO dbo.notificacoes (Id, DestinatarioId, Tipo, Titulo, Mensagem, CriadaEmUtc, ChaveOrigem, AvisoTentativas) VALUES (NEWID(), @u, 3, 't', 'm', SYSUTCDATETIME(), 'senha:x', 0)",
            ("@u", afetado));
        Assert.Equal(547, await ErroSqlAsync(cs, "INSERT INTO dbo.notificacoes (Id, DestinatarioId, Tipo, Titulo, Mensagem, CriadaEmUtc, ChaveOrigem, AvisoTentativas) VALUES (NEWID(), @u, 4, 't', 'm', SYSUTCDATETIME(), 'senha:y', 0)",
            ("@u", afetado)));

        const string inserirAuditoria = """
            INSERT INTO dbo.Usuarios_alteracoes_senha (Id, UsuarioResponsavelId, UsuarioAfetadoId, IpResponsavel, AlteradaEmUtc, TipoAlteracao, TraceId, SolicitacaoRecuperacaoId)
            VALUES (@id, @r, @a, '127.0.0.1', SYSUTCDATETIME(), @t, 'trace', @s)
            """;
        async Task<int?> AuditoriaAsync(Guid? r, Guid a, int tipo, Guid? s)
        {
            try
            {
                await EscalarAsync(cs, inserirAuditoria, ("@id", Guid.NewGuid()), ("@r", (object?)r ?? DBNull.Value), ("@a", a), ("@t", tipo), ("@s", (object?)s ?? DBNull.Value));
                return null;
            }
            catch (SqlException ex) { return ex.Number; }
        }

        var recuperacao = Guid.NewGuid();
        await EscalarAsync(cs, """
            INSERT INTO dbo.Usuarios_recuperacoes_senha (Id, UsuarioId, Origem, Status, SolicitadaEmUtc, UltimaSolicitacaoEmUtc, Solicitacoes, IpSolicitacao, ExpiraEmUtc)
            VALUES (@id, @a, 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1, '127.0.0.1', DATEADD(DAY, 7, SYSUTCDATETIME()))
            """, ("@id", recuperacao), ("@a", afetado));
        Assert.Null(await AuditoriaAsync(afetado, afetado, 1, null));
        Assert.Equal(547, await AuditoriaAsync(null, afetado, 1, null));
        Assert.Equal(547, await AuditoriaAsync(responsavel, afetado, 1, null));
        Assert.Null(await AuditoriaAsync(responsavel, afetado, 2, null));
        Assert.Equal(547, await AuditoriaAsync(afetado, afetado, 2, null));
        Assert.Equal(547, await AuditoriaAsync(responsavel, afetado, 3, null));
        Assert.Null(await AuditoriaAsync(responsavel, afetado, 3, recuperacao));
        Assert.Equal(2601, await AuditoriaAsync(responsavel, afetado, 2, recuperacao));
        Assert.Equal(547, await AuditoriaAsync(responsavel, afetado, 4, Guid.NewGuid()));
        Assert.Equal(547, await AuditoriaAsync(null, afetado, 4, null));
        Assert.Equal(547, await AuditoriaAsync(responsavel, afetado, 5, null));
        Assert.Equal(547, await AuditoriaAsync(Guid.NewGuid(), afetado, 2, null));

        // Somente inserção: UPDATE e DELETE são recusados pelo trigger, e o usuário referenciado não pode ser apagado.
        Assert.Equal(50160, await ErroSqlAsync(cs, "UPDATE dbo.Usuarios_alteracoes_senha SET IpResponsavel = '10.0.0.1'"));
        Assert.Equal(50160, await ErroSqlAsync(cs, "DELETE FROM dbo.Usuarios_alteracoes_senha"));
        Assert.Equal(3, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.Usuarios_alteracoes_senha"));
        Assert.Equal(547, await ErroSqlAsync(cs, "DELETE FROM dbo.Usuarios WHERE Id = @a", ("@a", afetado)));

        // Recuperação: uma aberta por usuário; autorização inteira ou nada; status coerente.
        Assert.Equal(2601, await ErroSqlAsync(cs, """
            INSERT INTO dbo.Usuarios_recuperacoes_senha (Id, UsuarioId, Origem, Status, SolicitadaEmUtc, UltimaSolicitacaoEmUtc, Solicitacoes, IpSolicitacao, ExpiraEmUtc)
            VALUES (NEWID(), @a, 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1, '127.0.0.1', SYSUTCDATETIME())
            """, ("@a", afetado)));
        Assert.Equal(547, await ErroSqlAsync(cs, "UPDATE dbo.Usuarios_recuperacoes_senha SET Status = 2 WHERE Id = @id", ("@id", recuperacao)));
        Assert.Equal(547, await ErroSqlAsync(cs, "UPDATE dbo.Usuarios_recuperacoes_senha SET TokenHash = 0x01, TokenExpiraEmUtc = SYSUTCDATETIME(), SecurityVersion = 7 WHERE Id = @id",
            ("@id", recuperacao)));
        Assert.Equal(547, await ErroSqlAsync(cs, "UPDATE dbo.Usuarios_recuperacoes_senha SET Status = 3 WHERE Id = @id", ("@id", recuperacao)));
        Assert.Equal(547, await ErroSqlAsync(cs, "UPDATE dbo.Usuarios_recuperacoes_senha SET Origem = 2 WHERE Id = @id", ("@id", recuperacao)));
        Assert.Equal(547, await ErroSqlAsync(cs, "UPDATE dbo.Usuarios_recuperacoes_senha SET MetodoVerificacao = 9 WHERE Id = @id", ("@id", recuperacao)));
        Assert.Equal(547, await ErroSqlAsync(cs, "UPDATE dbo.Usuarios_recuperacoes_senha SET Solicitacoes = 0 WHERE Id = @id", ("@id", recuperacao)));

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal(DBNull.Value, await EscalarAsync(cs, "SELECT OBJECT_ID('dbo.Usuarios_alteracoes_senha')"));
        Assert.Equal(DBNull.Value, await EscalarAsync(cs, "SELECT OBJECT_ID('dbo.Usuarios_recuperacoes_senha')"));
        Assert.Equal(0, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.notificacoes WHERE Tipo = 3"));
        Assert.Equal(2, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.notificacoes WHERE Tipo = 1"));
        Assert.Equal(547, await ErroSqlAsync(cs, "INSERT INTO dbo.notificacoes (Id, DestinatarioId, Tipo, Titulo, Mensagem, CriadaEmUtc, ChaveOrigem, AvisoTentativas) VALUES (NEWID(), @u, 3, 't', 'm', SYSUTCDATETIME(), 'senha:z', 0)",
            ("@u", afetado)));

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
        }
    }
}
