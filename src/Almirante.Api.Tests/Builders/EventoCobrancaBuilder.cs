using Almirante.Api.Dtos;
using Almirante.Api.Entities;

namespace Almirante.Api.Tests.Builders;

public sealed class EventoCobrancaBuilder
{
    private decimal _valor = 252m;
    private StatusLancamento _status = StatusLancamento.Pendente;
    private decimal? _valorPago;
    private bool _ajustado;

    public static EventoCobrancaBuilder De(decimal valor) => new() { _valor = valor };

    public static List<EventoLancamentoDto> Repetir(int quantidade, decimal valor) =>
        Enumerable.Range(0, quantidade).Select(_ => De(valor).Build()).ToList();

    public EventoCobrancaBuilder Ajustada()
    {
        _ajustado = true;
        return this;
    }

    public EventoCobrancaBuilder Atrasada()
    {
        _status = StatusLancamento.Atrasado;
        return this;
    }

    public EventoCobrancaBuilder PagaCom(decimal valorPago)
    {
        _status = StatusLancamento.Pago;
        _valorPago = valorPago;
        return this;
    }

    public EventoCobrancaBuilder PagoHistorico()
    {
        _status = StatusLancamento.Pago;
        _valorPago = null;
        return this;
    }

    public EventoLancamentoDto Build() => new(Guid.NewGuid(), Guid.NewGuid(), _valor, _status, _valorPago, _ajustado);
}
