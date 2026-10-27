using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosCinExpiracaoEnderecoMigrationTests
{
    private const string MigrationAnterior = "20261016191302_AddUsuariosRgTipoSanguineoDoencas";
    private const string MigrationEmTeste = "20261017191727_AddUsuariosCinExpiracaoEndereco";

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
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='198.51.100.9';
            -- O trigger grava AlteradoEmUtc = SYSUTCDATETIME(): sem a pausa, dois UPDATEs seguidos podem cair no mesmo
            -- tique do relógio e o OrderBy(AlteradoEmUtc) dos asserts sai em ordem aleatória.
            WAITFOR DELAY '00:00:00.020';
            UPDATE dbo.Usuarios SET {set} WHERE Id = @id;
            """, ("@resp", responsavel), ("@id", id));

    private const string ContarColunasNovas = """
        SELECT COUNT(*) FROM sys.columns
        WHERE object_id IN (OBJECT_ID('dbo.Usuarios'), OBJECT_ID('dbo._usuarios_hist'))
          AND (name IN ('EhCin', 'DataExpiracao') OR name LIKE 'Endereco%')
        """;

    private const string EnderecoCompleto = """
        EnderecoCep = '04382160', EnderecoLogradouro = 'Rua Fictícia', EnderecoNumero = '10', EnderecoComplemento = 'Casa',
        EnderecoComplementoCep = 'lado ímpar', EnderecoBairro = 'Bairro Teste', EnderecoLocalidade = 'São Paulo', EnderecoUf = 'SP',
        EnderecoEstado = 'São Paulo', EnderecoRegiao = 'Sudeste', EnderecoIbge = '3550308', EnderecoGia = '1004', EnderecoDdd = '11',
        EnderecoSiafi = '7107'
        """;

    [Fact]
    public async Task Up_PreservaOLegado_SemInventarDados_AuditaOsCamposNovos_EDownRestauraOTriggerAnterior()
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
                    INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Ativo, Rg)
                    VALUES (@id, @nome, @email, UPPER(@email), N'x', @cargo, SYSUTCDATETIME(), 0, 0, 1, '1234567')
                    """, ("@id", id), ("@nome", nome), ("@email", $"{id:N}@local.dev"), ("@cargo", cargo.Id));
            }

            await AtualizarAuditadoAsync(cs, responsavel, "Rg = '7654321'", legado);
            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using (var db = Contexto(cs))
        {
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
            var salvo = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == legado);
            Assert.Equal(("7654321", false, null, null, null, null), (salvo.Rg, salvo.EhCin, salvo.DataExpiracao, salvo.EnderecoCep, salvo.EnderecoNumero, salvo.EnderecoLocalidade));
            var antigo = await db.UsuariosHistorico.AsNoTracking().SingleAsync(h => h.UsuarioId == legado);
            Assert.Equal(("1234567", null, null, null), (antigo.Rg, antigo.EhCin, antigo.DataExpiracao, antigo.EnderecoCep));
        }

        Assert.Equal(1, await AtualizarAuditadoAsync(cs, responsavel, "EhCin = 1", legado));
        await AtualizarAuditadoAsync(cs, responsavel, "DataExpiracao = '2032-01-15'", legado);
        await AtualizarAuditadoAsync(cs, responsavel, EnderecoCompleto, legado);
        await AtualizarAuditadoAsync(cs, responsavel, "EnderecoSiafi = '0001'", legado);
        await AtualizarAuditadoAsync(cs, responsavel, "EnderecoSiafi = '0001'", legado);
        await using (var db = Contexto(cs))
        {
            var historico = await db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == legado).OrderBy(h => h.AlteradoEmUtc).ToListAsync();
            Assert.Equal(5, historico.Count);
            Assert.Equal((false, null), (historico[1].EhCin, historico[1].DataExpiracao));
            Assert.Equal((true, null), (historico[2].EhCin, historico[2].DataExpiracao));
            Assert.Equal((true, new DateOnly(2032, 1, 15), null), (historico[3].EhCin, historico[3].DataExpiracao, historico[3].EnderecoCep));
            Assert.Equal(("04382160", "Rua Fictícia", "10", "Casa", "lado ímpar", "São Paulo", "SP", "7107"),
                (historico[4].EnderecoCep, historico[4].EnderecoLogradouro, historico[4].EnderecoNumero, historico[4].EnderecoComplemento,
                    historico[4].EnderecoComplementoCep, historico[4].EnderecoLocalidade, historico[4].EnderecoUf, historico[4].EnderecoSiafi));
            Assert.All(historico.Skip(1), h => Assert.Equal((responsavel, "198.51.100.9"), (h.UsuarioResponsavelId, h.IpResponsavel)));
        }

        var erro = await Assert.ThrowsAsync<SqlException>(() => AtualizarAuditadoAsync(cs, responsavel, "EnderecoCep = NULL", legado));
        Assert.Equal(547, erro.Number);

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal(0, await ContarAsync(cs, ContarColunasNovas));
        Assert.Equal(0, await ContarAsync(cs, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Usuarios_Endereco'"));
        await AtualizarAuditadoAsync(cs, responsavel, "Rg = '1111111'", legado);
        Assert.Equal(6, await ContarAsync(cs, $"SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = '{legado}'"));

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }

        Assert.Equal(34, await ContarAsync(cs, ContarColunasNovas));
    }
}
