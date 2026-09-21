using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentosEnumMigrationTests
{
    private const string MigrationAnterior = "20260917220217_AddAtualizadoPorUsuarioIdToLancamentos";

    [Fact]
    public async Task Migration_PreservaDadosAuditoriaEReplayLegado_EPermiteRollback()
    {
        using var factory = new SqlServerApiFactory();
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(factory.ConnectionString).Options;
        await using var db = new AlmiranteDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationAnterior);

        var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Admin", Descricao = "Teste", CriadoPor = "Teste", Role = "ADM" };
        var membro = new Usuario
        {
            Id = Guid.NewGuid(), Nome = "Legado enums", Email = "enums@local.dev", EmailNormalizado = "ENUMS@LOCAL.DEV",
            SenhaHash = "nao-utilizado", CargoId = cargo.Id,
        };
        db.Cargos.Add(cargo);
        db.Usuarios.Add(membro);
        await db.SaveChangesAsync();

        var operacaoId = Guid.NewGuid();
        var key = $"legado-{Guid.NewGuid():N}";
        // Fixture do contrato histórico, independente da implementação atual do hash.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Mensalidade|Legado|Clube|Despesa|50.00|2099-10-01")));
        var vencimento = new DateTime(2099, 10, 1);
        var criadoEm = new DateTime(2026, 1, 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO LancamentosOperacoes
                (Id, IdempotencyKey, RequestHash, Finalidade, Descricao, Categoria, TipoFluxo, Valor, Vencimento,
                 UsuariosProcessados, LancamentosCriados, CriadoPorUsuarioId, CriadoEmUtc)
            VALUES ({operacaoId}, {key}, {hash}, N'Mensalidade', N'Legado', N'Clube', N'Despesa', 50, {vencimento}, 3, 3, {membro.Id}, {criadoEm});
            """);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var categorias = new[] { "Evento", "Clube", "Clube" };
        var fluxos = new[] { "Entrada", "Despesa", "Despesa" };
        var statuses = new[] { "Pendente", "Pago", "Atrasado" };
        for (var i = 0; i < ids.Length; i++)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Lancamentos
                    (Id, MembroId, Finalidade, Descricao, Categoria, TipoFluxo, Valor, Vencimento, Status, DataCriacao, Ativo, OperacaoId)
                VALUES ({ids[i]}, {membro.Id}, N'Mensalidade', N'Legado', {categorias[i]}, {fluxos[i]}, 50, {vencimento}, {statuses[i]}, {criadoEm}, 1, {operacaoId});
                """);
        }
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={membro.Id};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=N'198.51.100.15';
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=N'Auditoria legada';
            UPDATE Lancamentos SET Ativo=0 WHERE Id={ids[2]};
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=NULL;
            """);
        await db.Database.CloseConnectionAsync();

        await migrator.MigrateAsync();
        var expectedCategorias = new[] { CategoriaLancamento.Evento, CategoriaLancamento.Clube, CategoriaLancamento.Clube };
        var expectedFluxos = new[] { TipoFluxoLancamento.Entrada, TipoFluxoLancamento.Saida, TipoFluxoLancamento.Saida };
        var expectedStatuses = new[] { StatusLancamento.Pendente, StatusLancamento.Pago, StatusLancamento.Atrasado };
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var lancamento = await db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id);
            Assert.Equal(expectedCategorias[i], lancamento.Categoria);
            Assert.Equal(expectedFluxos[i], lancamento.TipoFluxo);
            Assert.Equal(expectedStatuses[i], lancamento.Status);
            Assert.Equal(50m, lancamento.Valor);
            Assert.Equal(criadoEm, lancamento.DataCriacao);
        }
        var auditoria = await db.LancamentosDeletados.AsNoTracking().SingleAsync();
        Assert.Equal(CategoriaLancamento.Clube, auditoria.Categoria);
        Assert.Equal(TipoFluxoLancamento.Saida, auditoria.TipoFluxo);
        Assert.Equal(StatusLancamento.Atrasado, auditoria.Status);
        Assert.Equal("Auditoria legada", auditoria.Motivo);
        var operacao = await db.LancamentosOperacoes.AsNoTracking().SingleAsync();
        Assert.Equal(CategoriaLancamento.Clube, operacao.Categoria);
        Assert.Equal(TipoFluxoLancamento.Saida, operacao.TipoFluxo);
        Assert.Equal(hash, operacao.RequestHash);

        // As oito colunas foram convertidas, não apenas o modelo CLR.
        Assert.Equal(8, await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME IN ('Lancamentos', 'LancamentosOperacoes', 'lancamentos_deletados')
                AND COLUMN_NAME IN ('Categoria', 'Status', 'TipoFluxo') AND DATA_TYPE='int'
            """).SingleAsync());
        var invalid = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Lancamentos SET Status=3 WHERE Id={ids[0]}"));
        Assert.Equal(547, invalid.Number);

        await migrator.MigrateAsync(MigrationAnterior);
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal($"{categorias[i]}|{fluxos[i]}|{statuses[i]}", await db.Database.SqlQuery<string>(
                $"SELECT Categoria + '|' + TipoFluxo + '|' + Status AS Value FROM Lancamentos WHERE Id={ids[i]}").SingleAsync());
        }
        Assert.Equal("Clube|Despesa|Atrasado", await db.Database.SqlQueryRaw<string>(
            "SELECT Categoria + '|' + TipoFluxo + '|' + Status AS Value FROM lancamentos_deletados").SingleAsync());
        Assert.Equal("Clube|Despesa", await db.Database.SqlQueryRaw<string>(
            "SELECT Categoria + '|' + TipoFluxo AS Value FROM LancamentosOperacoes").SingleAsync());
        await migrator.MigrateAsync();

        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar")
        {
            Content = JsonContent.Create(new
            {
                finalidade = "Mensalidade", descricao = "Legado", categoria = "Clube", tipoFluxo = "Saida",
                valor = 50m, vencimento = "2099-10-01", aplicarATodosOsMembros = true,
            }),
        };
        replay.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(operacaoId, (await response.Content.ReadFromJsonAsync<LancamentoGeralResponse>())!.OperacaoId);
        Assert.Equal(3, await db.Lancamentos.CountAsync());

        var list = await client.GetFromJsonAsync<LancamentosResponse>("/api/Lancamentos?search=clu&status=1");
        Assert.Equal(ids[1], Assert.Single(list!.Items).Id);
        var updated = await client.PutAsJsonAsync($"/api/Lancamentos/{ids[0]}", new { status = 2, categoria = 1, tipoFluxo = 1 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{ids[0]}")
        {
            Content = JsonContent.Create(new { motivo = "Após migração" }),
        };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        var novaAuditoria = await db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == ids[0]);
        Assert.Equal(StatusLancamento.Atrasado, novaAuditoria.Status);
        Assert.Equal(CategoriaLancamento.Clube, novaAuditoria.Categoria);
        Assert.Equal(TipoFluxoLancamento.Saida, novaAuditoria.TipoFluxo);
    }

    [Fact]
    public async Task Migration_DadoLegadoInvalido_AbortaSemConverterParcialmente()
    {
        using var factory = new SqlServerApiFactory();
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(factory.ConnectionString).Options;
        await using var db = new AlmiranteDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationAnterior);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Lancamentos (Id, Finalidade, Categoria, TipoFluxo, Valor, Vencimento, Status, DataCriacao, Ativo)
            VALUES ({id}, N'Mensalidade', N'Categoria desconhecida', N'Entrada', 50, '2099-10-01', N'Pendente', '2026-01-01', 1);
            """);

        var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync());

        Assert.Equal(50002, error.Number);
        Assert.Equal(MigrationAnterior, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal("Categoria desconhecida|Entrada|Pendente", await db.Database.SqlQuery<string>(
            $"SELECT Categoria + '|' + TipoFluxo + '|' + Status AS Value FROM Lancamentos WHERE Id={id}").SingleAsync());
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS Value FROM sys.check_constraints
            WHERE name IN ('CK_Lancamentos_TipoFluxo', 'CK_LancamentosOperacoes_TipoFluxo', 'CK_lancamentos_deletados_TipoFluxo')
            """).SingleAsync());
    }
}
