using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosResponsavelFotoMigrationTests
{
    private const string MigrationAnterior = "20261001112715_AddUsuariosDadosPessoais";
    private const string MigrationEmTeste = "20261016103555_AddUsuariosResponsavelEFoto";

    private static AlmiranteDbContext Contexto(string connectionString) =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(connectionString).Options);

    private static async Task<int> ExecAsync(string connectionString, string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<object?> EscalarAsync(string connectionString, string sql)
    {
        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteScalarAsync();
    }

    private static Task<int> AtualizarAuditadoAsync(string cs, Guid responsavel, string set, Guid id, params (string, object)[] parametros) =>
        ExecAsync(cs, $"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='198.51.100.7';
            -- O trigger grava AlteradoEmUtc = SYSUTCDATETIME(): sem a pausa, dois UPDATEs seguidos podem cair no mesmo
            -- tique do relógio e o OrderBy(AlteradoEmUtc) dos asserts sai em ordem aleatória.
            WAITFOR DELAY '00:00:00.020';
            UPDATE dbo.Usuarios SET {set} WHERE Id = @id;
            """, [("@resp", responsavel), ("@id", id), .. parametros]);

    private static Task<int> ContarColunasNovasAsync(string cs) => EscalarAsync(cs, """
        SELECT COUNT(*) FROM sys.columns
        WHERE object_id IN (OBJECT_ID('dbo.Usuarios'), OBJECT_ID('dbo._usuarios_hist'))
          AND name IN ('ResponsavelNome', 'ResponsavelEmail', 'ResponsavelCpf', 'ResponsavelTelefone', 'FotoCaminho')
        """).ContinueWith(t => (int)t.Result!);

    [Fact]
    public async Task Up_PreservaOLegado_AuditaOsCamposNovos_EDownRestauraOTriggerAnterior()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        Guid legado, responsavel;
        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Membro do clube.", CriadoPor = "Teste", Role = "DS" };
            db.Cargos.Add(cargo);
            await db.SaveChangesAsync();
            legado = Guid.NewGuid();
            responsavel = Guid.NewGuid();
            foreach (var (id, nome) in new[] { (legado, "Legado"), (responsavel, "Responsável") })
            {
                await ExecAsync(cs, """
                    INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Ativo)
                    VALUES (@id, @nome, @email, UPPER(@email), N'x', @cargo, SYSUTCDATETIME(), 0, 0, 1)
                    """, ("@id", id), ("@nome", nome), ("@email", $"{id:N}@local.dev"), ("@cargo", cargo.Id));
            }

            await AtualizarAuditadoAsync(cs, responsavel, "Telefone = '1134567890'", legado);
            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using (var db = Contexto(cs))
        {
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
            var salvo = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == legado);
            Assert.Equal("1134567890", salvo.Telefone);
            Assert.Null(salvo.ResponsavelNome);
            Assert.Null(salvo.FotoCaminho);
            Assert.Null((await db.UsuariosHistorico.AsNoTracking().SingleAsync(h => h.UsuarioId == legado)).ResponsavelNome);
        }

        var foto = FotoChave.Gerar(legado);
        await AtualizarAuditadoAsync(cs, responsavel, "ResponsavelNome = 'Mãe Legada', FotoCaminho = @foto", legado, ("@foto", foto));
        await AtualizarAuditadoAsync(cs, responsavel, "FotoCaminho = NULL", legado);
        await using (var db = Contexto(cs))
        {
            var historico = await db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == legado).OrderBy(h => h.AlteradoEmUtc).ToListAsync();
            Assert.Equal(3, historico.Count);
            Assert.Equal((null, null), (historico[1].ResponsavelNome, historico[1].FotoCaminho));
            Assert.Equal(("Mãe Legada", foto), (historico[2].ResponsavelNome, historico[2].FotoCaminho));
        }

        var erro = await Assert.ThrowsAsync<SqlException>(() => AtualizarAuditadoAsync(cs, responsavel, "FotoCaminho = @foto", legado, ("@foto", FotoChave.Gerar(responsavel))));
        Assert.Equal(547, erro.Number);

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal(0, await ContarColunasNovasAsync(cs));
        Assert.Equal(0, (int)(await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Usuarios_FotoCaminho'"))!);
        await AtualizarAuditadoAsync(cs, responsavel, "Telefone = '2134567890'", legado);
        Assert.Equal(4, (int)(await EscalarAsync(cs, $"SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = '{legado}'"))!);

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }

        Assert.Equal(10, await ContarColunasNovasAsync(cs));
    }

    [Fact]
    public async Task Up_EmBancoForaDaPagina1252_Interrompe_SemCriarAsColunas()
    {
        var servidor = Environment.GetEnvironmentVariable(SqlServerApiFactory.EnvironmentVariable)
            ?? throw new InvalidOperationException($"Defina {SqlServerApiFactory.EnvironmentVariable}.");
        var banco = $"almirante_test_cirilico_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(servidor) { InitialCatalog = "master" }.ConnectionString;
        await ExecAsync(master, $"CREATE DATABASE [{banco}] COLLATE Cyrillic_General_CI_AS");
        try
        {
            var cs = new SqlConnectionStringBuilder(servidor) { InitialCatalog = banco }.ConnectionString;
            await using (var db = Contexto(cs))
            {
                var erro = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync());
                Assert.Equal(50034, erro.Number);
                Assert.Contains("1252", erro.Message);
                Assert.DoesNotContain(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
                Assert.Contains(MigrationAnterior, await db.Database.GetAppliedMigrationsAsync());
            }

            Assert.Equal(0, await ContarColunasNovasAsync(cs));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var conexaoMaster = new SqlConnection(master);
            await conexaoMaster.OpenAsync();
            await BancosDeTeste.RemoverAsync(conexaoMaster, banco);
        }
    }
}
