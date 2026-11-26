using Almirante.Api.Entities;

namespace Almirante.Api.Services;

public static class PagamentosRegistrados
{
    public static IQueryable<Lancamento> Ate(IQueryable<Lancamento> lancamentos, DateOnly final) =>
        lancamentos.Where(l => l.Ativo && l.Status == StatusLancamento.Pago && l.DataPagamento != null && l.ValorPago != null
            && l.DataPagamento <= final);

    public static IQueryable<Lancamento> Entre(IQueryable<Lancamento> lancamentos, DateOnly inicial, DateOnly final) =>
        Ate(lancamentos, final).Where(l => l.DataPagamento >= inicial);
}
