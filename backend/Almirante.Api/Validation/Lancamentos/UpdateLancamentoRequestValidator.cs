using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

public sealed class UpdateLancamentoRequestValidator : AbstractValidator<UpdateLancamentoRequest>
{
    public UpdateLancamentoRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Finalidade!).LancamentoFinalidadeValida().When(x => x.Finalidade is not null);
        RuleFor(x => x.Categoria!.Value).IsInEnum().When(x => x.Categoria.HasValue);
        RuleFor(x => x.TipoFluxo!.Value).IsInEnum().When(x => x.TipoFluxo.HasValue);
        RuleFor(x => x.Status!.Value).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.Valor!.Value).LancamentoValorValido().When(x => x.Valor.HasValue);
        RuleFor(x => x.Vencimento!.Value)
            .GreaterThanOrEqualTo(_ => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            .When(x => x.Vencimento.HasValue);
    }
}
