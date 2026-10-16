using Almirante.Api.Entities;
using FluentValidation;
namespace Almirante.Api.Validation.Lancamentos;
public static class LancamentoValidationRules
{
    public static IRuleBuilderOptions<T,string> LancamentoFinalidadeValida<T>(this IRuleBuilder<T,string> rule) =>
        rule.NotEmpty().Must(LancamentoFinalidades.Todas.Contains).WithMessage("Finalidade inválida.");
    public static IRuleBuilderOptions<T,decimal> LancamentoValorValido<T>(this IRuleBuilder<T,decimal> rule) =>
        rule.GreaterThan(0).WithMessage("Valor deve ser maior que zero.");
}
