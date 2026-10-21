using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Almirante.Api.Validation.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class DashboardService(AlmiranteDbContext db, DataCivil dataCivil)
{
    /// <summary>Quantidade máxima de passeios em proximosEventos.</summary>
    public const int LimiteProximosEventos = 5;

    public async Task<DashboardResponse> ObterAsync(GetDashboardQuery query, CancellationToken ct)
    {
        var hoje = dataCivil.Hoje();
        var (inicial, final) = query.DataInicial is not null && query.DataFinal is not null
            ? (GetDashboardQueryValidator.Ler(query.DataInicial), GetDashboardQueryValidator.Ler(query.DataFinal))
            : (new DateOnly(hoje.Year, hoje.Month, 1), new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(1).AddDays(-1));

        var totais = await db.Lancamentos.AsNoTracking()
            .Where(l => l.Ativo && l.Status == StatusLancamento.Pago && l.DataPagamento != null && l.ValorPago != null
                && l.DataPagamento >= inicial && l.DataPagamento <= final)
            .GroupBy(l => l.TipoFluxo)
            .Select(g => new { TipoFluxo = g.Key, Total = g.Sum(l => l.ValorPago!.Value) })
            .ToListAsync(ct);
        var entradas = totais.Where(t => t.TipoFluxo == TipoFluxoLancamento.Entrada).Sum(t => t.Total);
        var saidas = totais.Where(t => t.TipoFluxo == TipoFluxoLancamento.Saida).Sum(t => t.Total);

        var desbravadores = await db.Usuarios.AsNoTracking()
            .CountAsync(u => u.Ativo && u.Cargo!.Role == Roles.Desbravador, ct);

        return new DashboardResponse
        {
            Periodo = new DashboardPeriodoDto(inicial, final),
            Financeiro = new DashboardFinanceiroDto(entradas - saidas, entradas, saidas),
            Desbravadores = new DashboardDesbravadoresDto(desbravadores),
            ProximosEventos = await ProximosEventosAsync(hoje, ct),
        };
    }

    private async Task<IReadOnlyList<DashboardEventoDto>> ProximosEventosAsync(DateOnly hoje, CancellationToken ct)
    {
        var passeios = await db.Eventos.AsNoTracking()
            .Where(e => e.Ativo && e.DataEvento >= hoje)
            .GroupBy(e => e.EventoGrupoId)
            .Select(g => new { GrupoId = g.Key, Data = g.Min(e => e.DataEvento), Local = g.Min(e => e.Local)! })
            .OrderBy(p => p.Data).ThenBy(p => p.Local).ThenBy(p => p.GrupoId)
            .Take(LimiteProximosEventos)
            .ToListAsync(ct);
        if (passeios.Count == 0)
        {
            return [];
        }

        var grupos = passeios.Select(p => p.GrupoId).ToList();
        var cadastros = await db.Eventos.AsNoTracking()
            .Where(e => e.Ativo && grupos.Contains(e.EventoGrupoId))
            .Select(e => new { e.Id, e.EventoGrupoId, Participantes = e.Membros.Count(m => m.Ativo) })
            .ToListAsync(ct);
        var porGrupo = cadastros.ToLookup(c => c.EventoGrupoId);

        return passeios.Select(p => new DashboardEventoDto
        {
            EventoGrupoId = p.GrupoId,
            EventoIds = porGrupo[p.GrupoId].Select(c => c.Id).Order().ToList(),
            Data = p.Data,
            Local = p.Local,
            QuantidadeParticipantes = porGrupo[p.GrupoId].Sum(c => c.Participantes),
        }).ToList();
    }
}
