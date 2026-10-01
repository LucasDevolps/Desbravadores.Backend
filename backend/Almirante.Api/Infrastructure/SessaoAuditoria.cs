using Almirante.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Infrastructure;

// SESSION_CONTEXT lido pelos triggers TR_eventos_AuditoriaExclusaoLogica, TR_Lancamentos_AuditoriaExclusaoLogica e
// TR_Usuarios_Historico (este último usa só o responsável e o IP).
// O responsável vem das claims validadas e o IP de HttpContext.Connection.RemoteIpAddress (controller); nunca do
// corpo da requisição. Deve ser usada com a conexão FIXADA (OpenConnectionAsync) para que a definição e a limpeza
// aconteçam na mesma conexão física; se a limpeza falhar, quem chama descarta o pool (SqlConnection.ClearPool).
public sealed class SessaoAuditoria(AlmiranteDbContext db)
{
    private bool _definido;

    // Executa o trabalho em uma transação, sobre uma conexão FIXADA (OpenConnection): o SESSION_CONTEXT
    // definido para os triggers e a limpeza no finally acontecem na MESMA conexão física. Se a limpeza falhar,
    // o pool da conexão é descartado para não reaproveitar identidade/IP de outra operação.
    // A execution strategy (retry do Aspire) exige que a unidade de trabalho inteira esteja no delegate.
    // Exige provedor relacional (SQL Server): quem chama recusa o InMemory com a própria mensagem.
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
                        // Marcar a conexão ainda emprestada para descarte ANTES de devolvê-la ao pool.
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
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={usuarioId};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value={ip};
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value={motivo};
            """, ct);
    }

    // true = nada a limpar ou limpo com sucesso.
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
