using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentosPagamentoMigrationTests
{
    private const string MigrationAnterior = "20261017191727_AddUsuariosCinExpiracaoEndereco";
    private const string MigrationEmTeste = "20261020121054_AddLancamentosPagamento";

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

    private static Task<object?> ExcluirAuditadoAsync(string cs, Guid responsavel, Guid lancamento) =>
        EscalarAsync(cs, """
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='198.51.100.7';
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=N'migração';
            UPDATE dbo.Lancamentos SET Ativo = 0 WHERE Id = @id;
            """, ("@resp", responsavel), ("@id", lancamento));

    private static Task<object?> InserirLancamentoAsync(string cs, Guid id, Guid membro, int status, DateTime? dataAtualizacao) =>
        EscalarAsync(cs, """
            INSERT INTO dbo.Lancamentos (Id, MembroId, Finalidade, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, DataAtualizacao, AtualizadoPorUsuarioId, Ativo)
            VALUES (@id, @membro, 'Mensalidade', 1, 50.00, '2026-09-10', @status, 0, '2026-09-01', @atualizacao, @atualizadoPor, 1)
            """, ("@id", id), ("@membro", membro), ("@status", status),
            ("@atualizacao", (object?)dataAtualizacao ?? DBNull.Value), ("@atualizadoPor", dataAtualizacao is null ? DBNull.Value : membro));

    [Fact]
    public async Task Up_PreservaPagosHistoricosSemInventarDataOuValor_AuditaColunasNovas_EDownRestauraOTriggerAnterior()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        Guid membro, pagoHistorico = Guid.NewGuid(), pendente = Guid.NewGuid(), excluidoAntes = Guid.NewGuid();
        var atualizadoEm = new DateTime(2026, 9, 15, 13, 45, 0, DateTimeKind.Utc);
        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Tesoureiro", Descricao = "Finanças.", CriadoPor = "Teste", Role = "TES" };
            db.Cargos.Add(cargo);
            await db.SaveChangesAsync();
            membro = Guid.NewGuid();
            await EscalarAsync(cs, """
                INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas, Ativo)
                VALUES (@id, N'Tesouraria', @email, UPPER(@email), N'x', @cargo, SYSUTCDATETIME(), 0, 0, 1)
                """, ("@id", membro), ("@email", $"{membro:N}@local.dev"), ("@cargo", cargo.Id));
            await InserirLancamentoAsync(cs, pagoHistorico, membro, (int)StatusLancamento.Pago, atualizadoEm);
            await InserirLancamentoAsync(cs, pendente, membro, (int)StatusLancamento.Pendente, null);
            await InserirLancamentoAsync(cs, excluidoAntes, membro, (int)StatusLancamento.Pago, null);
            await ExcluirAuditadoAsync(cs, membro, excluidoAntes);

            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using (var db = Contexto(cs))
        {
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
            var historico = await db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == pagoHistorico);
            Assert.Equal((StatusLancamento.Pago, 50m, atualizadoEm, (DateOnly?)null, (decimal?)null, (Guid?)null, (DateTime?)null),
                (historico.Status, historico.Valor, historico.DataAtualizacao!.Value, historico.DataPagamento, historico.ValorPago, historico.PagamentoRegistradoPorUsuarioId, historico.PagamentoRegistradoEmUtc));
            var aberto = await db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == pendente);
            Assert.Equal((StatusLancamento.Pendente, (DateOnly?)null, (decimal?)null), (aberto.Status, aberto.DataPagamento, aberto.ValorPago));
            var auditoriaAntiga = await db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == excluidoAntes);
            Assert.Equal(((DateOnly?)null, (decimal?)null), (auditoriaAntiga.DataPagamento, auditoriaAntiga.ValorPago));

            Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Lancamentos_Pagamento'"));
            Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Lancamentos_DataPagamento' AND has_filter = 1"));

            await EscalarAsync(cs, """
                UPDATE dbo.Lancamentos SET DataPagamento = '2026-09-20', ValorPago = 45.00, PagamentoRegistradoPorUsuarioId = @m, PagamentoRegistradoEmUtc = SYSUTCDATETIME()
                WHERE Id = @id
                """, ("@m", membro), ("@id", pagoHistorico));
            await ExcluirAuditadoAsync(cs, membro, pagoHistorico);
            var auditoria = await db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == pagoHistorico);
            Assert.Equal((new DateOnly(2026, 9, 20), 45m, (Guid?)membro), (auditoria.DataPagamento!.Value, auditoria.ValorPago!.Value, auditoria.PagamentoRegistradoPorUsuarioId));

            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal(0, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id IN (OBJECT_ID('dbo.Lancamentos'), OBJECT_ID('dbo.lancamentos_deletados'))
              AND name IN ('DataPagamento', 'ValorPago', 'PagamentoRegistradoPorUsuarioId', 'PagamentoRegistradoEmUtc')
            """));
        await ExcluirAuditadoAsync(cs, membro, pendente);
        Assert.Equal(1, await EscalarAsync(cs, "SELECT COUNT(*) FROM dbo.lancamentos_deletados WHERE LancamentoId = @id", ("@id", pendente)));
    }
}
