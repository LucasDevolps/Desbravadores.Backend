using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentoExclusaoService(AlmiranteDbContext db, TimeProvider clock, ILogger<LancamentoExclusaoService> logger)
{
    public async Task<bool> DeleteAsync(DeleteLancamentoRequest request, CancellationToken ct)
    {
        var vinculo = await db.Lancamentos.AsNoTracking()
            .Where(l => l.Id == request.Id && l.Ativo && l.EventoId != null)
            .Select(l => new { EventoId = l.EventoId!.Value, l.LancamentoOrigemId })
            .SingleOrDefaultAsync(ct);
        if (vinculo is not null)
        {
            if (vinculo.LancamentoOrigemId is not null)
            {
                throw LancamentosService.DevolucaoConflito();
            }

            if (!db.Database.IsRelational())
            {
                throw LancamentosService.LancamentoDeEventoConflito();
            }

            return await IsentarAsync(request, vinculo.EventoId, ct);
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

        return await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, request.Motivo.Trim(), ct);
            return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Lancamentos SET Ativo=0 WHERE Id={request.Id} AND Ativo=1;", ct) > 0;
        }, ct);
    }

    private Task<bool> IsentarAsync(DeleteLancamentoRequest request, Guid eventoId, CancellationToken ct)
    {
        var motivo = request.Motivo.Trim();
        var usuarioId = request.UsuarioResponsavelId;
        return SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            var agora = clock.GetUtcNow().UtcDateTime;
            var eventos = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.eventos SET AtualizadoEmUtc = {agora}, AtualizadoPorUsuarioId = {usuarioId} WHERE Id = {eventoId} AND Ativo = 1", ct);
            if (eventos == 0)
            {
                return false;
            }

            var cobranca = await db.Lancamentos.AsNoTracking()
                .SingleOrDefaultAsync(l => l.Id == request.Id && l.Ativo && l.EventoId == eventoId, ct);
            if (cobranca is null)
            {
                return false;
            }

            if (cobranca.Status == StatusLancamento.Pago || cobranca.DataPagamento is not null)
            {
                throw ApiProblemException.Conflict("Cobrança com pagamento registrado.",
                    "Só a cobrança de evento sem nenhum pagamento, inclusive parcial, pode ser excluída como isenção. Nenhum pagamento é apagado: " +
                    "para devolver o que foi pago, use o estorno do participante (POST /api/Eventos/{id}/Participantes/{membroId}/Estorno).",
                    new Dictionary<string, object?> { ["codigo"] = LancamentosService.CodigoCobrancaComPagamento, ["lancamentoId"] = cobranca.Id });
            }

            var participante = await db.EventosMembros
                .SingleOrDefaultAsync(m => m.EventoId == eventoId && m.LancamentoId == cobranca.Id && m.Ativo, ct)
                ?? throw ApiProblemException.Conflict("Cobrança sem participante ativo.",
                    "A cobrança não está ligada a um participante ativo do evento. Consulte o evento e tente novamente.");

            await sessao.DefinirContextoAuditoriaAsync(usuarioId, request.IpResponsavel, motivo, ct);
            var desativadas = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Lancamentos SET Ativo = 0, DataAtualizacao = {agora}, AtualizadoPorUsuarioId = {usuarioId} WHERE Id = {cobranca.Id} AND Ativo = 1 AND Status <> {(int)StatusLancamento.Pago} AND DataPagamento IS NULL", ct);
            if (desativadas != 1)
            {
                throw ApiProblemException.Conflict("Lançamento alterado por outra operação.",
                    "A cobrança mudou durante a requisição. Consulte o lançamento e tente novamente.");
            }

            participante.LancamentoId = null;
            participante.Isento = true;
            participante.IsencaoLancamentoId = cobranca.Id;
            participante.IsencaoMotivo = motivo;
            participante.IsentoPorUsuarioId = usuarioId;
            participante.IsentoEmUtc = agora;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Cobrança {LancamentoId} do membro {MembroId} no evento {EventoId} isenta por {UsuarioId}.",
                cobranca.Id, participante.MembroId, eventoId, usuarioId);
            return true;
        }, ct);
    }
}
