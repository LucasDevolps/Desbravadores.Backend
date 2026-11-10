using Almirante.Api.Entities;

namespace Almirante.Api.Services;

/// <summary>
/// Regra única do financeiro realizado (Dashboard e Tesouraria): lançamentos ativos com status Pago, dataPagamento e valorPago
/// registrados e dataPagamento no período (limites inclusivos). "Pago" históricos sem dados de pagamento ficam de fora.
/// </summary>
public static class PagamentosRegistrados
{
    public static IQueryable<Lancamento> Entre(IQueryable<Lancamento> lancamentos, DateOnly inicial, DateOnly final) =>
        lancamentos.Where(l => l.Ativo && l.Status == StatusLancamento.Pago && l.DataPagamento != null && l.ValorPago != null
            && l.DataPagamento >= inicial && l.DataPagamento <= final);
}
