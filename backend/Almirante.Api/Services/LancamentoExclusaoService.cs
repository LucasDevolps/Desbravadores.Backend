using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentoExclusaoService(AlmiranteDbContext db, ILogger<LancamentoExclusaoService> logger)
{
    public async Task<bool> DeleteAsync(DeleteLancamentoRequest request, CancellationToken ct)
    {
        // Excluir isoladamente um lançamento de evento desalinharia participantes, total e valor por membro.
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

        // Conexão fixada: SESSION_CONTEXT definido e limpo na MESMA conexão física (ver SessaoAuditoria).
        var sessao = new SessaoAuditoria(db);
        await db.Database.OpenConnectionAsync(ct);
        var conexao = db.Database.GetDbConnection() as SqlConnection;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, request.Motivo.Trim(), ct);
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
    }
}
