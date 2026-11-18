using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Almirante.Api.Validation.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class DashboardService(AlmiranteDbContext db, DataCivil dataCivil)
{
    public const int LimiteProximosEventos = 5;

    // Janela das atividades planejadas: hoje e os 29 dias civis seguintes.
    public const int DiasAtividadesPlanejadas = 30;

    // Janela das atividades recentes: os 30 dias civis anteriores a hoje (hoje fica nos próximos eventos).
    public const int DiasAtividadesRecentes = 30;
    public const int LimiteAtividadesRecentes = 5;

    public async Task<DashboardResponse> ObterAsync(GetDashboardQuery query, CancellationToken ct)
    {
        var hoje = dataCivil.Hoje();
        var (inicial, final) = query.DataInicial is not null && query.DataFinal is not null
            ? (GetDashboardQueryValidator.Ler(query.DataInicial), GetDashboardQueryValidator.Ler(query.DataFinal))
            : (new DateOnly(hoje.Year, hoje.Month, 1), new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(1).AddDays(-1));

        var desbravadores = await db.Usuarios.AsNoTracking()
            .CountAsync(u => u.Ativo && u.Cargo!.Role == Roles.Desbravador, ct);

        return new DashboardResponse
        {
            Periodo = new DashboardPeriodoDto(inicial, final),
            Financeiro = query.IncluirFinanceiro ? await FinanceiroAsync(inicial, final, ct) : null,
            Desbravadores = new DashboardDesbravadoresDto(desbravadores),
            ProximosEventos = await ProximosEventosAsync(hoje, ct),
            AtividadesPlanejadas = await AtividadesPlanejadasAsync(hoje, ct),
            AtividadesRecentes = await AtividadesRecentesAsync(hoje, ct),
        };
    }

    private async Task<DashboardFinanceiroDto> FinanceiroAsync(DateOnly inicial, DateOnly final, CancellationToken ct)
    {
        var totais = await PagamentosRegistrados.Entre(db.Lancamentos.AsNoTracking(), inicial, final)
            .GroupBy(l => l.TipoFluxo)
            .Select(g => new { TipoFluxo = g.Key, Total = g.Sum(l => l.ValorPago!.Value) })
            .ToListAsync(ct);
        var entradas = totais.Where(t => t.TipoFluxo == TipoFluxoLancamento.Entrada).Sum(t => t.Total);
        var saidas = totais.Where(t => t.TipoFluxo == TipoFluxoLancamento.Saida).Sum(t => t.Total);
        return new DashboardFinanceiroDto(entradas - saidas, entradas, saidas);
    }

    private IQueryable<Evento> Ativos(DateOnly inicial, DateOnly? final = null)
    {
        var eventos = db.Eventos.AsNoTracking().Where(e => e.Ativo && e.DataEvento >= inicial);
        return final is { } limite ? eventos.Where(e => e.DataEvento <= limite) : eventos;
    }

    private async Task<DashboardAtividadesPlanejadasDto> AtividadesPlanejadasAsync(DateOnly hoje, CancellationToken ct)
    {
        var final = hoje.AddDays(DiasAtividadesPlanejadas - 1);
        var quantidade = await Ativos(hoje, final).Select(e => e.EventoGrupoId).Distinct().CountAsync(ct);
        return new DashboardAtividadesPlanejadasDto(hoje, final, quantidade);
    }

    private async Task<IReadOnlyList<DashboardEventoDto>> ProximosEventosAsync(DateOnly hoje, CancellationToken ct)
    {
        var passeios = await Ativos(hoje)
            .GroupBy(e => e.EventoGrupoId)
            .Select(g => new Passeio
            {
                GrupoId = g.Key, Data = g.Min(e => e.DataEvento), Titulo = g.Min(e => e.Titulo)!, Local = g.Min(e => e.Local)!,
            })
            .OrderBy(p => p.Data).ThenBy(p => p.Local).ThenBy(p => p.GrupoId)
            .Take(LimiteProximosEventos)
            .ToListAsync(ct);
        return await DetalharAsync(passeios, ct);
    }

    private async Task<IReadOnlyList<DashboardEventoDto>> AtividadesRecentesAsync(DateOnly hoje, CancellationToken ct)
    {
        var passeios = await Ativos(hoje.AddDays(-DiasAtividadesRecentes), hoje.AddDays(-1))
            .GroupBy(e => e.EventoGrupoId)
            .Select(g => new Passeio
            {
                GrupoId = g.Key, Data = g.Max(e => e.DataEvento), Titulo = g.Min(e => e.Titulo)!, Local = g.Min(e => e.Local)!,
            })
            .OrderByDescending(p => p.Data).ThenBy(p => p.Local).ThenBy(p => p.GrupoId)
            .Take(LimiteAtividadesRecentes)
            .ToListAsync(ct);
        return await DetalharAsync(passeios, ct);
    }

    private async Task<IReadOnlyList<DashboardEventoDto>> DetalharAsync(List<Passeio> passeios, CancellationToken ct)
    {
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
            Titulo = p.Titulo,
            Local = p.Local,
            QuantidadeParticipantes = porGrupo[p.GrupoId].Sum(c => c.Participantes),
        }).ToList();
    }

    private sealed class Passeio
    {
        public Guid GrupoId { get; init; }
        public DateOnly Data { get; init; }
        public required string Titulo { get; init; }
        public required string Local { get; init; }
    }
}
