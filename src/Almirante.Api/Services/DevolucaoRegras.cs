using System.Globalization;
using Almirante.Api.Entities;

namespace Almirante.Api.Services;

/// <summary>
/// Regras puras das devoluções de evento (issue #209). Uma devolução é um lançamento de Saída do mesmo membro e evento, ligado ao
/// lançamento Pago de origem, com o valor esperado de volta. O pagamento original nunca muda: o que já voltou (ou está para voltar) é
/// a soma dos valores esperados das devoluções ativas daquele pagamento.
/// </summary>
public static class DevolucaoRegras
{
    public const int JustificativaMinimo = 3;

    /// <summary>Devolução gerada quando o valor por membro cai: o que foi pago acima do novo valor, descontado o que já foi devolvido.</summary>
    public static decimal PorReducao(decimal valorPago, decimal novoValorPorMembro, decimal jaDevolvido) =>
        Math.Max(0m, valorPago - novoValorPorMembro - jaDevolvido);

    /// <summary>Valor do pagamento que ainda fica com o clube (estorno avulso devolve exatamente isto).</summary>
    public static decimal Retido(decimal valorPago, decimal jaDevolvido) => Math.Max(0m, valorPago - jaDevolvido);

    /// <summary>
    /// Pagamento de evento cujo valor ainda fica, no todo ou em parte, com o clube: sem dado de liquidação ("Pago" histórico) conta como
    /// retido, porque não se sabe quanto entrou.
    /// </summary>
    public static bool TemValorRetido(Lancamento pago, IEnumerable<Lancamento> devolucoesDoPagamento) =>
        pago.ValorPago is not { } valorPago || Retido(valorPago, devolucoesDoPagamento.Sum(d => d.Valor)) > 0;

    /// <summary>Estorno concluído: nada retido e todas as devoluções do pagamento já baixadas.</summary>
    public static bool EstornoConcluido(Lancamento pago, IReadOnlyCollection<Lancamento> devolucoesDoPagamento) =>
        !TemValorRetido(pago, devolucoesDoPagamento) && devolucoesDoPagamento.All(d => d.Status == StatusLancamento.Pago);

    /// <summary>Texto vazio ou nulo é "não informada"; qualquer outro é trimado e precisa passar por <see cref="JustificativaValida"/>.</summary>
    public static string? NormalizarJustificativa(string? justificativa) =>
        string.IsNullOrEmpty(justificativa) ? null : justificativa.Trim();

    /// <summary>Pelo menos <see cref="JustificativaMinimo"/> caracteres sem contar os espaços ("a b" não passa; "abc" passa).</summary>
    public static bool JustificativaValida(string justificativa) =>
        justificativa.Count(c => !char.IsWhiteSpace(c)) >= JustificativaMinimo;

    /// <summary>A justificativa só pode faltar quando o valor devolvido é exatamente o esperado.</summary>
    public static bool ExigeJustificativa(decimal valorEsperado, decimal valorDevolvido) => valorDevolvido != valorEsperado;

    public static string Descricao(string tituloEvento, DateOnly dataEvento)
    {
        var texto = $"Devolução - Evento - {tituloEvento} - {dataEvento.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
        return texto.Length <= 500 ? texto : texto[..500];
    }

    public static Lancamento Nova(Lancamento origem, decimal valor, string tituloEvento, DateOnly dataEvento, DateOnly vencimento,
        string motivo, DateTime agoraUtc) => new()
    {
        Id = Guid.NewGuid(),
        MembroId = origem.MembroId,
        Finalidade = null,
        Descricao = Descricao(tituloEvento, dataEvento),
        Categoria = CategoriaLancamento.Evento,
        Valor = valor,
        Vencimento = vencimento,
        Status = StatusLancamento.Pendente,
        TipoFluxo = TipoFluxoLancamento.Saida,
        DataCriacao = agoraUtc,
        Ativo = true,
        EventoId = origem.EventoId,
        LancamentoOrigemId = origem.Id,
        DevolucaoMotivo = motivo,
    };
}
