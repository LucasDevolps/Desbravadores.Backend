using System.Globalization;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Resources;
using Almirante.Api.Services;

namespace Almirante.Api.Tests;

/// <summary>Issue #209: regras puras da devolução de evento, a notificação em .resx e o mapeamento do evento com devoluções.</summary>
public sealed class DevolucoesUnitTests
{
    private static readonly DateTime Agora = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static Lancamento Pago(decimal valor, decimal? valorPago, Guid? membro = null) => new()
    {
        Id = Guid.NewGuid(),
        MembroId = membro ?? Guid.NewGuid(),
        Categoria = CategoriaLancamento.Evento,
        TipoFluxo = TipoFluxoLancamento.Entrada,
        Valor = valor,
        Status = StatusLancamento.Pago,
        ValorPago = valorPago,
        DataPagamento = valorPago is null ? null : new DateOnly(2026, 11, 10),
        EventoId = Guid.NewGuid(),
    };

    private static Lancamento Devolucao(Lancamento origem, decimal valor, StatusLancamento status = StatusLancamento.Pendente)
    {
        var devolucao = DevolucaoRegras.Nova(origem, valor, "Passeio", new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 17), "reducao", Agora);
        devolucao.Status = status;
        return devolucao;
    }

    [Theory]
    [InlineData(20, 10, 0, 10)]
    [InlineData(20, 0, 0, 20)]
    [InlineData(20, 12, 8, 0)]
    [InlineData(20, 0, 8, 12)]
    [InlineData(5, 10, 0, 0)]
    [InlineData(20, 20, 0, 0)]
    [InlineData(20, 15, 10, 0)]
    public void PorReducao_DevolveSoOQueFoiPagoAcimaDoNovoValor_DescontandoOJaDevolvido(decimal valorPago, decimal novoValor, decimal jaDevolvido, decimal esperado) =>
        Assert.Equal(esperado, DevolucaoRegras.PorReducao(valorPago, novoValor, jaDevolvido));

    [Theory]
    [InlineData(20, 0, 20)]
    [InlineData(20, 8, 12)]
    [InlineData(20, 20, 0)]
    [InlineData(20, 25, 0)]
    public void Retido_EhOValorPagoMenosAsDevolucoes_NuncaNegativo(decimal valorPago, decimal jaDevolvido, decimal esperado) =>
        Assert.Equal(esperado, DevolucaoRegras.Retido(valorPago, jaDevolvido));

    [Fact]
    public void TemValorRetido_PagoHistoricoSemDadosContaComoRetido()
    {
        Assert.True(DevolucaoRegras.TemValorRetido(Pago(20m, null), []));
    }

    [Fact]
    public void TemValorRetido_ConsideraOValorEsperadoDasDevolucoes()
    {
        var pago = Pago(20m, 20m);
        Assert.True(DevolucaoRegras.TemValorRetido(pago, []));
        Assert.True(DevolucaoRegras.TemValorRetido(pago, [Devolucao(pago, 8m)]));
        Assert.False(DevolucaoRegras.TemValorRetido(pago, [Devolucao(pago, 8m), Devolucao(pago, 12m)]));
    }

    [Fact]
    public void EstornoConcluido_ExigeNadaRetidoETodasAsDevolucoesBaixadas()
    {
        var pago = Pago(20m, 20m);

        Assert.False(DevolucaoRegras.EstornoConcluido(pago, []));
        Assert.False(DevolucaoRegras.EstornoConcluido(pago, [Devolucao(pago, 20m)]));
        Assert.False(DevolucaoRegras.EstornoConcluido(pago, [Devolucao(pago, 10m, StatusLancamento.Pago)]));
        Assert.True(DevolucaoRegras.EstornoConcluido(pago, [Devolucao(pago, 20m, StatusLancamento.Pago)]));
        Assert.True(DevolucaoRegras.EstornoConcluido(pago, [Devolucao(pago, 5m, StatusLancamento.Pago), Devolucao(pago, 15m, StatusLancamento.Pago)]));
        Assert.False(DevolucaoRegras.EstornoConcluido(Pago(20m, null), []));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  abc  ", "abc")]
    [InlineData("   ", "")]
    public void NormalizarJustificativa_VazioEhNaoInformada_ORestoEhTrimado(string? entrada, string? esperado) =>
        Assert.Equal(esperado, DevolucaoRegras.NormalizarJustificativa(entrada));

    [Theory]
    [InlineData("abc", true)]
    [InlineData("a b c", true)]
    [InlineData("Pix devolvido", true)]
    [InlineData("a b", false)]
    [InlineData("ab", false)]
    [InlineData("", false)]
    [InlineData("      ", false)]
    [InlineData("a\tb", false)]
    public void JustificativaValida_ContaPeloMenos3CaracteresSemEspacos(string texto, bool esperado) =>
        Assert.Equal(esperado, DevolucaoRegras.JustificativaValida(texto));

    [Theory]
    [InlineData(20, 20, false)]
    [InlineData(20, 19.99, true)]
    [InlineData(20, 5, true)]
    public void ExigeJustificativa_SoQuandoOValorDifereDoEsperado(decimal esperado, decimal devolvido, bool exige) =>
        Assert.Equal(exige, DevolucaoRegras.ExigeJustificativa(esperado, devolvido));

    [Fact]
    public void Descricao_SegueOPadraoDoEvento_ELimitaA500Caracteres()
    {
        Assert.Equal("Devolução - Evento - Passeio - 01/12/2026", DevolucaoRegras.Descricao("Passeio", new DateOnly(2026, 12, 1)));
        Assert.Equal(500, DevolucaoRegras.Descricao(new string('x', 600), new DateOnly(2026, 12, 1)).Length);
    }

    [Fact]
    public void Nova_EhSaidaPendenteDoMembroLigadaAoPagamentoEAoEvento()
    {
        var origem = Pago(20m, 20m);

        var devolucao = DevolucaoRegras.Nova(origem, 12.5m, "Passeio", new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 17), "valor reduzido", Agora);

        Assert.NotEqual(Guid.Empty, devolucao.Id);
        Assert.Equal((origem.MembroId, origem.EventoId, (Guid?)origem.Id), (devolucao.MembroId, devolucao.EventoId, devolucao.LancamentoOrigemId));
        Assert.Equal((TipoFluxoLancamento.Saida, StatusLancamento.Pendente, CategoriaLancamento.Evento, 12.5m),
            (devolucao.TipoFluxo, devolucao.Status, devolucao.Categoria, devolucao.Valor));
        Assert.Equal((new DateOnly(2026, 11, 17), Agora, true), (devolucao.Vencimento, devolucao.DataCriacao, devolucao.Ativo));
        Assert.Equal(("valor reduzido", (string?)null, (string?)null), (devolucao.DevolucaoMotivo, devolucao.DevolucaoJustificativa, devolucao.Finalidade));
        Assert.Equal("Devolução - Evento - Passeio - 01/12/2026", devolucao.Descricao);
        Assert.Null(devolucao.ValorPago);
    }

    [Fact]
    public void NotificacaoDeDevolucao_TextoVemDoResx_ComValorEmReaisETituloDoEvento()
    {
        var origem = Pago(2000m, 2000m);
        var devolucao = DevolucaoRegras.Nova(origem, 1234.5m, "Acampamento", new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 17), "zerado", Agora);

        var notificacao = NotificacoesFabrica.DevolucaoEvento(TestHelpers.TextosNotificacoes(), devolucao, "Acampamento", Agora);

        Assert.Equal((origem.MembroId!.Value, TipoNotificacao.DevolucaoEvento, $"devolucao:{devolucao.Id:N}"),
            (notificacao.DestinatarioId, notificacao.Tipo, notificacao.ChaveOrigem));
        Assert.Equal("Devolução: Acampamento", notificacao.Titulo);
        Assert.Equal("Seu valor de R$ 1.234,50 referente ao evento Acampamento será devolvido.", notificacao.Mensagem);
        Assert.Equal((origem.EventoId, (Guid?)devolucao.Id), (notificacao.EventoId, notificacao.LancamentoId));
        Assert.Equal(((AvisoNotificacao?)AvisoNotificacao.Criada, Agora, Agora), (notificacao.AvisoPendente, notificacao.CriadaEmUtc, notificacao.AvisoProximaTentativaEmUtc));
    }

    [Fact]
    public void TextosDoResx_SoTemPortugues_EOutraCulturaCaiNoRecursoNeutro()
    {
        var textos = TestHelpers.TextosNotificacoes();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var titulo = textos[NotificacoesTextos.DevolucaoEventoTitulo, "X"];

            Assert.False(titulo.ResourceNotFound);
            Assert.Equal("Devolução: X", titulo.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Mapeamento_SeparaDevolucoesDasCobrancas_EOrdenaPorMembroEVencimento()
    {
        var evento = new Evento { Id = Guid.NewGuid(), Titulo = "Passeio", Local = "Parque", Versao = new byte[8] };
        var m1 = new Guid("00000000-0000-0000-0000-000000000001");
        var m2 = new Guid("00000000-0000-0000-0000-000000000002");
        var pago1 = Pago(20m, 20m, m1);
        var pago2 = Pago(20m, 20m, m2);
        var tardia = DevolucaoRegras.Nova(pago1, 5m, "Passeio", new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 20), "b", Agora);
        var cedo = DevolucaoRegras.Nova(pago1, 3m, "Passeio", new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 10), "a", Agora);
        var outra = DevolucaoRegras.Nova(pago2, 7m, "Passeio", new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 1), "c", Agora);
        outra.Status = StatusLancamento.Pago;
        outra.ValorPago = 6m;
        outra.DataPagamento = new DateOnly(2026, 11, 2);
        outra.DevolucaoJustificativa = "taxa do banco";

        var dto = EventoMapping.ToDto(evento, [m2, m1], [], new[] { outra, tardia, cedo }.Select(EventoMapping.ToDevolucaoDto));

        Assert.Equal([cedo.Id, tardia.Id, outra.Id], dto.Devolucoes.Select(d => d.Id));
        Assert.Equal(new EventoDevolucaoDto(outra.Id, m2, pago2.Id, 7m, new DateOnly(2026, 11, 1), StatusLancamento.Pago, 6m,
            new DateOnly(2026, 11, 2), "c", "taxa do banco"), dto.Devolucoes[2]);
        Assert.Empty(EventoMapping.ToDto(evento, [m1], []).Devolucoes);
    }
}
