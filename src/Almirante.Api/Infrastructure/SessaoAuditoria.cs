using System.Data;
using Almirante.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Infrastructure;

public sealed class SessaoAuditoria(AlmiranteDbContext db)
{
    private bool _definido;

    public static async Task<T> ExecutarEmTransacaoAsync<T>(AlmiranteDbContext db, ILogger logger,
        Func<SessaoAuditoria, Task<T>> trabalho, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var sessao = new SessaoAuditoria(db);
            await db.Database.OpenConnectionAsync(ct);
            var conexao = db.Database.GetDbConnection() as SqlConnection;
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var resultado = await trabalho(sessao);
                await tx.CommitAsync(ct);
                return resultado;
            }
            finally
            {
                try
                {
                    var limpo = await sessao.LimparAsync(logger);
                    if (!limpo && conexao is not null)
                    {
                        SqlConnection.ClearPool(conexao);
                    }
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            }
        });
    }

    public async Task DefinirContextoAuditoriaAsync(Guid usuarioId, string ip, string motivo, CancellationToken ct)
    {
        _definido = true;
        // varchar como as colunas de auditoria (IpResponsavel varchar(45), Motivo varchar(255)): os triggers leem com TRY_CAST.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={usuarioId};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value={new SqlParameter("@ip", SqlDbType.VarChar, 45) { Value = ip }};
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value={new SqlParameter("@motivo", SqlDbType.VarChar, 255) { Value = motivo }};
            """, ct);
    }

    public async Task<bool> LimparAsync(ILogger logger)
    {
        if (!_definido)
        {
            return true;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
                EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
                EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=NULL;
                """, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao limpar o SESSION_CONTEXT de auditoria; o pool de conexões será descartado.");
            return false;
        }
    }
}
