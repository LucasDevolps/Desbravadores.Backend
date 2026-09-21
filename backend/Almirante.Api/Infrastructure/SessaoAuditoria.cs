using Almirante.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Infrastructure;

// SESSION_CONTEXT lido pelos triggers TR_eventos_AuditoriaExclusaoLogica e TR_Lancamentos_AuditoriaExclusaoLogica.
// O responsável vem das claims validadas e o IP de HttpContext.Connection.RemoteIpAddress (controller); nunca do
// corpo da requisição. Deve ser usada com a conexão FIXADA (OpenConnectionAsync) para que a definição e a limpeza
// aconteçam na mesma conexão física; se a limpeza falhar, quem chama descarta o pool (SqlConnection.ClearPool).
public sealed class SessaoAuditoria(AlmiranteDbContext db)
{
    private bool _definido;

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
