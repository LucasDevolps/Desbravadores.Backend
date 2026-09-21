using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentoExclusaoService(AlmiranteDbContext db, ILogger<LancamentoExclusaoService> logger)
{
    public async Task<bool> DeleteAsync(DeleteLancamentoRequest request, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var entity = await db.Lancamentos.SingleOrDefaultAsync(l => l.Id == request.Id && l.Ativo, ct);
            if (entity is null)
            {
                return false;
            }

            entity.Ativo = false;
            await db.SaveChangesAsync(ct);
            return true;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={request.UsuarioResponsavelId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value={request.IpResponsavel};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value={request.Motivo.Trim()};", ct);
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Lancamentos SET Ativo=0 WHERE Id={request.Id} AND Ativo=1;", ct);
            if (affected == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            await tx.CommitAsync(ct);
            return true;
        }
        finally
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync("""
                    EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
                    EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
                    EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=NULL;
                    """, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao limpar SESSION_CONTEXT.");
            }
        }
    }
}
