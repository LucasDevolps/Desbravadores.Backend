using System.Globalization;
using Almirante.Api.Entities;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

// Regras reaproveitadas por vários validators de lançamento (CreateLancamentoRequestValidator,
// UpdateLancamentoRequestValidator, RegistrarLancamentoRequestValidator,
// CreateLancamentoGeralRequestValidator), para não repetir a mesma regra de negócio em cada um —
// equivalente ao antigo LancamentoValidacao, agora como extension methods do FluentValidation.
public static class LancamentoValidationRules
{
    public const string DateFormat = "yyyy-MM-dd";

    public static IRuleBuilderOptions<T, string> LancamentoTipoValido<T>(this IRuleBuilder<T, string> ruleBuilder) =>
        ruleBuilder.Must(tipo => LancamentoTipos.Todos.Contains(tipo))
            .WithMessage(tipo => $"Tipo inválido: {tipo}.");

    public static IRuleBuilderOptions<T, string> LancamentoCategoriaValida<T>(this IRuleBuilder<T, string> ruleBuilder) =>
        ruleBuilder.Must(categoria => LancamentoCategorias.Todas.Contains(categoria))
            .WithMessage(categoria => $"Categoria inválida: {categoria}.");

    public static IRuleBuilderOptions<T, string> LancamentoStatusValido<T>(this IRuleBuilder<T, string> ruleBuilder) =>
        ruleBuilder.Must(status => LancamentoStatuses.Todos.Contains(status))
            .WithMessage(status => $"Status inválido: {status}.");

    // Valor deve ser estritamente positivo: nem negativo, nem zero (um lançamento de R$ 0,00 não
    // representa nenhuma movimentação financeira real).
    public static IRuleBuilderOptions<T, decimal> LancamentoValorValido<T>(this IRuleBuilder<T, decimal> ruleBuilder) =>
        ruleBuilder.GreaterThan(0m).WithMessage("Valor deve ser maior que zero.");

    // Vencimento no formato yyyy-MM-dd e não pode ser uma data já passada (comparação por dia,
    // UTC do servidor — hoje é permitido, só datas estritamente anteriores são rejeitadas). Um
    // único Must (em vez de dois encadeados) evita chamar ParseExact sobre um formato ainda não
    // confirmado como válido.
    public static IRuleBuilderOptions<T, string> LancamentoVencimentoValido<T>(this IRuleBuilder<T, string> ruleBuilder) =>
        ruleBuilder.Must(VencimentoValidoENaoPassado)
            .WithMessage("Vencimento deve usar o formato yyyy-MM-dd e não pode ser uma data no passado.");

    private static bool VencimentoValidoENaoPassado(string vencimento) =>
        DateOnly.TryParseExact(vencimento, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        && parsed >= DateOnly.FromDateTime(DateTime.UtcNow);
}
