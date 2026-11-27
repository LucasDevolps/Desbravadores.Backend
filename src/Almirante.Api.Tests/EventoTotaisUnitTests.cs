using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using Almirante.Api.Tests.Builders;

namespace Almirante.Api.Tests;

public sealed class EventoTotaisUnitTests
{
    private static EventoDevolucaoDto Devolucao(decimal esperado, decimal? devolvido) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        esperado, DateOnly.FromDateTime(DateTime.UtcNow), devolvido is null ? StatusLancamento.Pendente : StatusLancamento.Pago, devolvido,
        devolvido is null ? null : DateOnly.FromDateTime(DateTime.UtcNow), "motivo", null);

    private static EventoTotais Totais(decimal valorPorMembro, IReadOnlyCollection<EventoLancamentoDto> cobrancas,
        params EventoDevolucaoDto[] devolucoes) => EventoRegras.Totais(valorPorMembro, cobrancas, devolucoes);

    [Fact]
    public void DezCobrancasSemAjuste_EstimaValorPorMembroVezesDez()
    {
        var totais = Totais(252m, EventoCobrancaBuilder.Repetir(10, 252m));

        Assert.Equal(new EventoTotais(2520m, 0m, 2520m, 0m), totais);
    }

    [Fact]
    public void AjusteDaAnaPara200_EstimaSomaDasCobrancasAtivas()
    {
        var cobrancas = EventoCobrancaBuilder.Repetir(9, 252m);
        cobrancas.Add(EventoCobrancaBuilder.De(200m).Ajustada().Build());

        Assert.Equal(new EventoTotais(2468m, 0m, 2468m, 0m), Totais(252m, cobrancas));
    }

    [Fact]
    public void PagamentoParcialDe80_ContaComoPagoEDeixaOSaldoAReceber()
    {
        var cobrancas = EventoCobrancaBuilder.Repetir(9, 252m);
        cobrancas.Add(EventoCobrancaBuilder.De(200m).Ajustada().PagaCom(80m).Build());

        Assert.Equal(new EventoTotais(2468m, 80m, 2388m, 0m), Totais(252m, cobrancas));
    }

    [Fact]
    public void CobrancaIsenta_SaiDoEstimadoPorNaoEstarMaisAtiva()
    {
        Assert.Equal(2268m, Totais(252m, EventoCobrancaBuilder.Repetir(9, 252m)).Estimado);
    }

    [Fact]
    public void CobrancaAtrasada_ContaNoValorAReceber()
    {
        var cobrancas = new[] { EventoCobrancaBuilder.De(100m).Atrasada().Build(), EventoCobrancaBuilder.De(100m).PagaCom(100m).Build() };

        Assert.Equal(new EventoTotais(200m, 100m, 100m, 0m), Totais(100m, cobrancas));
    }

    [Fact]
    public void PagoHistoricoSemValorPago_NaoPresumePagamentoNemReabreDivida()
    {
        var cobrancas = new[] { EventoCobrancaBuilder.De(50m).PagoHistorico().Build() };

        Assert.Equal(new EventoTotais(50m, 0m, 0m, 0m), Totais(50m, cobrancas));
    }

    [Fact]
    public void ReducaoDoEventoDepoisDoPagamento_EstimaONovoValorSemReabrirDivida()
    {
        var cobrancas = new[] { EventoCobrancaBuilder.De(50m).PagaCom(50m).Build(), EventoCobrancaBuilder.De(50m).PagaCom(30m).Build() };

        var totais = Totais(20m, cobrancas, Devolucao(30m, 30m), Devolucao(10m, null));

        Assert.Equal(new EventoTotais(40m, 80m, 0m, 30m), totais);
    }

    [Fact]
    public void CobrancaAjustada_NaoAcompanhaReducaoDoValorPorMembro()
    {
        var cobrancas = new[] { EventoCobrancaBuilder.De(200m).Ajustada().PagaCom(200m).Build() };

        Assert.Equal(new EventoTotais(200m, 200m, 0m, 0m), Totais(150m, cobrancas));
    }

    [Fact]
    public void EventoZeradoComPagamento_EstimaZeroEMantemOPagoSeparadoDaDevolucao()
    {
        var cobrancas = new[] { EventoCobrancaBuilder.De(20m).PagaCom(20m).Build() };

        Assert.Equal(new EventoTotais(0m, 20m, 0m, 20m), Totais(0m, cobrancas, Devolucao(20m, 20m)));
    }

    [Fact]
    public void EventoGratuito_SemCobrancas_TotaisZerados()
    {
        Assert.Equal(new EventoTotais(0m, 0m, 0m, 0m), Totais(0m, []));
    }
}
