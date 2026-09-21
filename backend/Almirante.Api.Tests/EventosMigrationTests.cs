using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

// Migration AddEventos (issue #56) aplicada e revertida sobre um banco JÁ populado, em SQL Server real:
// preserva os lançamentos legados, recria o trigger de auditoria com EventoId/Finalidade nula e o Down é
// possível mesmo com eventos e lançamentos de evento (Finalidade nula) existentes.
[Trait("Category", "RequiresSqlServer")]
public sealed class EventosMigrationTests
{
    private const string MigrationAnterior = "20260920030935_ConvertLancamentoEnums";
    private const string Migration = "20260921160617_AddEventos";

    private static async Task<T?> Escalar<T>(SqlConnection c, string sql, params (string, object)[] p)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default : (T)r;
    }

    private static async Task Exec(SqlConnection c, string sql, params (string, object)[] p)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AddEventos_PreservaDadosLegados_RecriaTrigger_EDownEUpFuncionamComEventos()
    {
        using var factory = new SqlServerApiFactory();
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(factory.ConnectionString).Options;
        var legado = Guid.NewGuid();
        Guid usuarioId;

        await using (var db = new AlmiranteDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(MigrationAnterior);

            var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Membro.", CriadoPor = "Teste", Role = "DS" };
            db.Cargos.Add(cargo);
            var usuario = new Usuario { Id = Guid.NewGuid(), Nome = "Legado", Email = "legado-ev@local.dev", EmailNormalizado = "LEGADO-EV@LOCAL.DEV", SenhaHash = "x", CargoId = cargo.Id };
            db.Usuarios.Add(usuario);
            await db.SaveChangesAsync();
            usuarioId = usuario.Id;

            // lançamento legado (enums inteiros, Finalidade preenchida) ANTES da migration
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO Lancamentos (Id, MembroId, Finalidade, Descricao, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, Ativo)
                VALUES ({legado}, {usuario.Id}, {"Mensalidade"}, {"legado"}, {1}, {30m}, {new DateTime(2026, 10, 1)}, {1}, {0}, {new DateTime(2026, 9, 1)}, {true})");

            await migrator.MigrateAsync(Migration);
        }

        await using var conexao = new SqlConnection(factory.ConnectionString);
        await conexao.OpenAsync();

        // dados legados intactos; Finalidade agora aceita NULL; EventoId nulo
        Assert.Equal("Mensalidade", await Escalar<string>(conexao, "SELECT Finalidade FROM Lancamentos WHERE Id = @id", ("@id", legado)));
        Assert.Null(await Escalar<Guid?>(conexao, "SELECT EventoId FROM Lancamentos WHERE Id = @id", ("@id", legado)));
        Assert.Equal("YES", await Escalar<string>(conexao, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Lancamentos' AND COLUMN_NAME = 'Finalidade'"));

        // trigger recriado: continua exigindo contexto e passa a copiar EventoId (nulo aqui)
        Assert.Equal(50001, (await Assert.ThrowsAsync<SqlException>(() => Exec(conexao, "UPDATE Lancamentos SET Ativo = 0 WHERE Id = @id", ("@id", legado)))).Number);
        await Exec(conexao, """
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=N'198.51.100.1';
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=N'legado';
            """, ("@u", usuarioId));
        await Exec(conexao, "UPDATE Lancamentos SET Ativo = 0 WHERE Id = @id", ("@id", legado));
        Assert.Equal("Mensalidade", await Escalar<string>(conexao, "SELECT Finalidade FROM lancamentos_deletados WHERE LancamentoId = @id", ("@id", legado)));
        Assert.Null(await Escalar<Guid?>(conexao, "SELECT EventoId FROM lancamentos_deletados WHERE LancamentoId = @id", ("@id", legado)));
        await Exec(conexao, """
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=NULL;
            """);

        // cria um evento e um lançamento de evento (Finalidade NULL) para provar o Down com dados
        var evento = Guid.NewGuid();
        var lancEvento = Guid.NewGuid();
        await Exec(conexao, """
            INSERT INTO eventos (Id, EventoGrupoId, DataEvento, [Local], TransporteEhGratis, TransporteValor, AlimentacaoIndividual, AlimentacaoValor,
                                 SeguroObrigatorio, ValorPorMembro, Ativo, CriadoPorUsuarioId, CriadoEmUtc)
            VALUES (@e, @e, '2026-10-18', N'Parque', 0, 10, 0, 8, 2, 20, 1, @u, SYSUTCDATETIME());
            INSERT INTO Lancamentos (Id, MembroId, Finalidade, Descricao, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, Ativo, EventoId)
            VALUES (@l, @u, NULL, N'evento', 0, 20, '2026-10-18', 0, 0, SYSUTCDATETIME(), 1, @e);
            """, ("@e", evento), ("@l", lancEvento), ("@u", usuarioId));
        await conexao.CloseAsync();
        SqlConnection.ClearAllPools();

        await using (var db = new AlmiranteDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();

            await migrator.MigrateAsync(MigrationAnterior); // Down com eventos existentes

            await using var c2 = new SqlConnection(factory.ConnectionString);
            await c2.OpenAsync();
            Assert.Null(await Escalar<int?>(c2, "SELECT OBJECT_ID('dbo.eventos')"));
            Assert.Equal("NO", await Escalar<string>(c2, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Lancamentos' AND COLUMN_NAME = 'Finalidade'"));
            Assert.Equal(string.Empty, await Escalar<string>(c2, "SELECT Finalidade FROM Lancamentos WHERE Id = @id", ("@id", lancEvento)));
            Assert.Equal(1, await Escalar<int>(c2, "SELECT COUNT(*) FROM sys.triggers WHERE name = 'TR_Lancamentos_AuditoriaExclusaoLogica'"));
            Assert.Equal(0, await Escalar<int>(c2, "SELECT COUNT(*) FROM sys.triggers WHERE name = 'TR_eventos_AuditoriaExclusaoLogica'"));
            Assert.Equal(0, await Escalar<int>(c2, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Lancamentos') AND name = 'EventoId'"));
            await c2.CloseAsync();

            await migrator.MigrateAsync(); // e novamente para a frente
        }

        await using var c3 = new SqlConnection(factory.ConnectionString);
        await c3.OpenAsync();
        Assert.Equal(1, await Escalar<int>(c3, "SELECT COUNT(*) FROM sys.triggers WHERE name = 'TR_eventos_AuditoriaExclusaoLogica'"));
        Assert.Equal(1, await Escalar<int>(c3, "SELECT COUNT(*) FROM sys.triggers WHERE name = 'TR_Lancamentos_AuditoriaExclusaoLogica'"));
        Assert.Equal(1, await Escalar<int>(c3, "SELECT COUNT(*) FROM dbo.Lancamentos WHERE Id = @id AND Finalidade = 'Mensalidade'", ("@id", legado)));
    }
    private const string MigrationResposta = "20260921222826_AddEventosOperacoesResposta";

    // Migration incremental que guarda a resposta original do POST (RespostaJson): o Up sobre um banco já populado na versão
    // anterior (eventos ativos e inativos, operações idempotentes antigas) preserva tudo e deixa as operações antigas SEM
    // resposta (não existe fonte confiável do resultado original); o Down descarta só as respostas e nada mais.
    [Fact]
    public async Task AddEventosOperacoesResposta_PreservaOperacoesAntigas_SemInventarSnapshot_EDownDescartaSoAsRespostas()
    {
        using var factory = new SqlServerApiFactory();
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(factory.ConnectionString).Options;
        var (ativo, inativo, operacaoAtiva, operacaoInativa) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Guid usuarioId;

        await using (var db = new AlmiranteDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(Migration); // versão anterior à correção da PR #62

            var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Membro.", CriadoPor = "Teste", Role = "DS" };
            db.Cargos.Add(cargo);
            var usuario = new Usuario { Id = Guid.NewGuid(), Nome = "Legado", Email = "legado-resp@local.dev", EmailNormalizado = "LEGADO-RESP@LOCAL.DEV", SenhaHash = "x", CargoId = cargo.Id };
            db.Usuarios.Add(usuario);
            await db.SaveChangesAsync();
            usuarioId = usuario.Id;
        }

        await using (var c = new SqlConnection(factory.ConnectionString))
        {
            await c.OpenAsync();
            await Exec(c, """
                INSERT INTO eventos (Id, EventoGrupoId, DataEvento, [Local], TransporteEhGratis, TransporteValor, AlimentacaoIndividual, AlimentacaoValor, SeguroObrigatorio, ValorPorMembro, Ativo, CriadoPorUsuarioId, CriadoEmUtc)
                VALUES (@a, @a, '2026-10-18', N'Parque', 0, 10, 0, 8, 2, 20, 1, @u, SYSUTCDATETIME()),
                       (@i, @i, '2026-10-19', N'Parque', 0, 10, 0, 8, 2, 20, 0, @u, SYSUTCDATETIME());
                INSERT INTO eventos_operacoes (Id, UsuarioId, IdempotencyKey, RequestHash, EventoId, CriadoEmUtc)
                VALUES (@oa, @u, N'chave-antiga-ativa', N'hash-a', @a, SYSUTCDATETIME()), (@oi, @u, N'chave-antiga-inativa', N'hash-i', @i, SYSUTCDATETIME());
                """, ("@a", ativo), ("@i", inativo), ("@u", usuarioId), ("@oa", operacaoAtiva), ("@oi", operacaoInativa));
        }
        SqlConnection.ClearAllPools();

        await using (var db = new AlmiranteDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationResposta);
        }

        await using (var c = new SqlConnection(factory.ConnectionString))
        {
            await c.OpenAsync();
            // Up: operações, chaves e vínculos intactos; a coluna existe, é anulável e nada foi inventado
            Assert.Equal(2, await Escalar<int>(c, "SELECT COUNT(*) FROM eventos_operacoes"));
            Assert.Equal(2, await Escalar<int>(c, "SELECT COUNT(*) FROM eventos_operacoes WHERE RespostaJson IS NULL"));
            Assert.Equal(ativo, await Escalar<Guid>(c, "SELECT EventoId FROM eventos_operacoes WHERE IdempotencyKey = N'chave-antiga-ativa'"));
            Assert.Equal(inativo, await Escalar<Guid>(c, "SELECT EventoId FROM eventos_operacoes WHERE IdempotencyKey = N'chave-antiga-inativa'"));
            Assert.Equal("YES", await Escalar<string>(c, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'eventos_operacoes' AND COLUMN_NAME = 'RespostaJson'"));
            Assert.Equal(-1, await Escalar<int>(c, "SELECT CAST(CHARACTER_MAXIMUM_LENGTH AS int) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'eventos_operacoes' AND COLUMN_NAME = 'RespostaJson'"));
            Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_eventos_operacoes_RespostaJson'"));
            // a constraint aceita JSON e recusa lixo
            await Exec(c, "UPDATE eventos_operacoes SET RespostaJson = N'{\"ok\":true}' WHERE Id = @o", ("@o", operacaoAtiva));
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => Exec(c, "UPDATE eventos_operacoes SET RespostaJson = N'não é json' WHERE Id = @o", ("@o", operacaoAtiva)))).Number);
        }
        SqlConnection.ClearAllPools();

        // Down: só a coluna e a constraint saem; operações, chaves, eventos e vínculos permanecem (as respostas se perdem)
        await using (var db = new AlmiranteDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(Migration);
        }

        await using (var c = new SqlConnection(factory.ConnectionString))
        {
            await c.OpenAsync();
            Assert.Equal(0, await Escalar<int>(c, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.eventos_operacoes') AND name = N'RespostaJson'"));
            Assert.Equal(0, await Escalar<int>(c, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_eventos_operacoes_RespostaJson'"));
            Assert.Equal(2, await Escalar<int>(c, "SELECT COUNT(*) FROM eventos_operacoes"));
            Assert.Equal(2, await Escalar<int>(c, "SELECT COUNT(*) FROM eventos"));
        }
        SqlConnection.ClearAllPools();

        // e para a frente de novo: as operações voltam ao estado "sem resposta original"
        await using (var db = new AlmiranteDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using var c3 = new SqlConnection(factory.ConnectionString);
        await c3.OpenAsync();
        Assert.Equal(2, await Escalar<int>(c3, "SELECT COUNT(*) FROM eventos_operacoes WHERE RespostaJson IS NULL"));
    }
}
