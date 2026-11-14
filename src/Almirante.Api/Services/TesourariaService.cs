using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class TesourariaService(AlmiranteDbContext db, DataCivil dataCivil)
{
    /// <summary>Competências de receitasDespesas: os cinco meses anteriores e o mês atual.</summary>
    public const int MESES_RECEITAS_DESPESAS = 6;

    public async Task<TesourariaResumoResponse> ObterResumoAsync(CancellationToken ct)
    {
        var hoje = dataCivil.Hoje();
        var mesAtual = new DateOnly(hoje.Year, hoje.Month, 1);
        var primeiroMes = mesAtual.AddMonths(1 - MESES_RECEITAS_DESPESAS);

        var totais = await PagamentosRegistrados.Entre(db.Lancamentos.AsNoTracking(), primeiroMes, mesAtual.AddMonths(1).AddDays(-1))
            .GroupBy(l => new { l.DataPagamento!.Value.Year, l.DataPagamento.Value.Month, l.TipoFluxo })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.TipoFluxo, Total = g.Sum(l => l.ValorPago!.Value) })
            .ToListAsync(ct);

        decimal Total(DateOnly mes, TipoFluxoLancamento fluxo) =>
            totais.Where(t => t.Year == mes.Year && t.Month == mes.Month && t.TipoFluxo == fluxo).Sum(t => t.Total);

        var receitasDespesas = Enumerable.Range(0, MESES_RECEITAS_DESPESAS)
            .Select(primeiroMes.AddMonths)
            .Select(mes => new TesourariaCompetenciaDto(mes.Year, mes.Month,
                Total(mes, TipoFluxoLancamento.Entrada), Total(mes, TipoFluxoLancamento.Saida)))
            .ToList();

        return new TesourariaResumoResponse { ReceitasDespesas = receitasDespesas };
    }
}
