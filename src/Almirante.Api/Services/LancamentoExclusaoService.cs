using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentoExclusaoService(AlmiranteDbContext db, ILogger<LancamentoExclusaoService> logger)
{
    public async Task<bool> DeleteAsync(DeleteLancamentoRequest request, CancellationToken ct)
    {
        if (await db.Lancamentos.AnyAsync(l => l.Id == request.Id && l.Ativo && l.EventoId != null, ct))
        {
            throw LancamentosService.LancamentoDeEventoConflito();
        }

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

        // Nenhuma linha atualizada (inexistente ou já excluído): nada mudou, e a transação confirma vazia.
        return await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, request.Motivo.Trim(), ct);
            return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Lancamentos SET Ativo=0 WHERE Id={request.Id} AND Ativo=1;", ct) > 0;
        }, ct);
    }
}
