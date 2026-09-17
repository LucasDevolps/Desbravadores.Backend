using Almirante.Api.Dtos;
using FluentValidation;
namespace Almirante.Api.Validation.Lancamentos;
public sealed class UpdateLancamentoRequestValidator : AbstractValidator<UpdateLancamentoRequest>
{
    public UpdateLancamentoRequestValidator(TimeProvider clock)
    {
        RuleFor(x=>x.Finalidade!).LancamentoFinalidadeValida().When(x=>x.Finalidade is not null);
        RuleFor(x=>x.Categoria!).LancamentoCategoriaValida().When(x=>x.Categoria is not null);
        RuleFor(x=>x.Status!).LancamentoStatusValido().When(x=>x.Status is not null);
        RuleFor(x=>x.Valor!.Value).LancamentoValorValido().When(x=>x.Valor.HasValue);
        RuleFor(x=>x.Vencimento!.Value).GreaterThanOrEqualTo(_=>DateOnly.FromDateTime(clock.GetLocalNow().DateTime)).When(x=>x.Vencimento.HasValue);
    }
}
