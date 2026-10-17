using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosRgTipoSanguineoDoencasMigrationTests
{
    private const string MigrationAnterior = "20261016103555_AddUsuariosResponsavelEFoto";
    private const string MigrationEmTeste = "20261016191302_AddUsuariosRgTipoSanguineoDoencas";

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

    private static async Task<int> ContarAsync(string connectionString, string sql)
    {
        await using var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static Task<int> AtualizarAuditadoAsync(string cs, Guid responsavel, string set, Guid id) =>
        ExecAsync(cs, $"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='198.51.100.8';
            UPDATE dbo.Usuarios SET {set} WHERE Id = @id;
            """, ("@resp", responsavel), ("@id", id));

    private const string ContarColunasNovas = """
        SELECT COUNT(*) FROM sys.columns
        WHERE object_id IN (OBJECT_ID('dbo.Usuarios'), OBJECT_ID('dbo._usuarios_hist')) AND name IN ('Rg', 'TipoSanguineo', 'DoencasAlergias')
        """;

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
            foreach (var (id, nome, cpf) in new[] { (legado, "Legado", "52998224725"), (responsavel, "Responsável", (string?)null) })
            {
                await ExecAsync(cs, """
                    INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Ativo, Cpf, Telefone)
                    VALUES (@id, @nome, @email, UPPER(@email), N'x', @cargo, SYSUTCDATETIME(), 0, 0, 1, @cpf, '1134567890')
                    """, ("@id", id), ("@nome", nome), ("@email", $"{id:N}@local.dev"), ("@cargo", cargo.Id), ("@cpf", (object?)cpf ?? DBNull.Value));
            }

            await AtualizarAuditadoAsync(cs, responsavel, "Telefone = '1134567891'", legado);
            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using (var db = Contexto(cs))
        {
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
            var salvo = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == legado);
            Assert.Equal(("52998224725", "1134567891"), (salvo.Cpf, salvo.Telefone));
            Assert.Equal((null, null, null), (salvo.Rg, salvo.TipoSanguineo, salvo.DoencasAlergias));
            var antigo = await db.UsuariosHistorico.AsNoTracking().SingleAsync(h => h.UsuarioId == legado);
            Assert.Equal(("1134567890", null, null), (antigo.Telefone, antigo.Rg, antigo.DoencasAlergias));
        }

        await AtualizarAuditadoAsync(cs, responsavel, "Rg = '1234567', TipoSanguineo = 'A+', DoencasAlergias = 'Alergia fictícia'", legado);
        await AtualizarAuditadoAsync(cs, responsavel, "DoencasAlergias = 'Alergia fictícia (revisada)'", legado);
        await using (var db = Contexto(cs))
        {
            var historico = await db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == legado).OrderBy(h => h.AlteradoEmUtc).ToListAsync();
            Assert.Equal(3, historico.Count);
            Assert.Equal((null, null, null), (historico[1].Rg, historico[1].TipoSanguineo, historico[1].DoencasAlergias));
            Assert.Equal(("1234567", "A+", "Alergia fictícia"), (historico[2].Rg, historico[2].TipoSanguineo, historico[2].DoencasAlergias));
        }

        var erro = await Assert.ThrowsAsync<SqlException>(() => AtualizarAuditadoAsync(cs, responsavel, "TipoSanguineo = 'Z+'", legado));
        Assert.Equal(547, erro.Number);

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal(0, await ContarAsync(cs, ContarColunasNovas));
        Assert.Equal(0, await ContarAsync(cs, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Usuarios_TipoSanguineo'"));
        await AtualizarAuditadoAsync(cs, responsavel, "Telefone = '2134567890'", legado);
        Assert.Equal(4, await ContarAsync(cs, $"SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = '{legado}'"));

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }

        Assert.Equal(6, await ContarAsync(cs, ContarColunasNovas));
    }
}
