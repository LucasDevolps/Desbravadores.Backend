using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class NotificacoesMigrationTests
{
    private const string MigrationAnterior = "20261020121054_AddLancamentosPagamento";
    private const string MigrationEmTeste = "20261021012556_AddNotificacoes";

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

    [Fact]
    public async Task Up_CriaTabelaVaziaComIndicesERestricoes_SemNotificacoesRetroativas_EDownRemove()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Membro.", CriadoPor = "Teste", Role = "DS" };
            db.Cargos.Add(cargo);
            await db.SaveChangesAsync();
            var membro = Guid.NewGuid();
            await EscalarAsync(cs, """
                INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Ativo)
                VALUES (@id, N'Membro', @email, UPPER(@email), N'x', @cargo, SYSUTCDATETIME(), 0, 0, 1);
                INSERT INTO dbo.Lancamentos (Id, MembroId, Finalidade, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, Ativo)
                VALUES (NEWID(), @id, 'Mensalidade', 1, 50.00, '2026-11-10', 0, 0, SYSUTCDATETIME(), 1);
                """, ("@id", membro), ("@email", $"{membro:N}@local.dev"), ("@cargo", cargo.Id));

            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using (var db = Contexto(cs))
        {
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(0, await db.Notificacoes.CountAsync());
            Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_notificacoes_destinatario_origem' AND is_unique = 1"));
            Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_notificacoes_pendentes' AND has_filter = 1"));
            Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_notificacoes_aviso_pendente' AND has_filter = 1"));
            Assert.Equal(3, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.notificacoes')"));
            Assert.Equal(0, await EscalarAsync(cs, """
                SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID('dbo.notificacoes') AND t.name IN ('nvarchar', 'nchar', 'ntext')
                """));

            var erro = await Assert.ThrowsAsync<SqlException>(() => EscalarAsync(cs, """
                INSERT INTO dbo.notificacoes (Id, DestinatarioId, Tipo, Titulo, Mensagem, CriadaEmUtc, ChaveOrigem, AvisoPendente, AvisoTentativas)
                SELECT NEWID(), Id, 1, 'x', 'y', SYSUTCDATETIME(), 'teste', 1, 0 FROM dbo.Usuarios
                """));
            Assert.Contains("CK_notificacoes_Aviso", erro.Message);

            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal(DBNull.Value, await EscalarAsync(cs, "SELECT OBJECT_ID('dbo.notificacoes')"));
    }
}
