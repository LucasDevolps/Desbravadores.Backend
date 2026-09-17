using Almirante.Api.Dtos;
using FluentValidation;
namespace Almirante.Api.Validation.Lancamentos;
public sealed class RegistrarLancamentoRequestValidator : AbstractValidator<RegistrarLancamentoRequest>
{
    public RegistrarLancamentoRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Finalidade).LancamentoFinalidadeValida();
        RuleFor(x => x.Categoria).LancamentoCategoriaValida();
        RuleFor(x => x.Valor).LancamentoValorValido();
        RuleFor(x => x.Vencimento).NotEmpty().WithMessage("Vencimento é obrigatório.")
            .GreaterThanOrEqualTo(_ => DateOnly.FromDateTime(clock.GetLocalNow().DateTime))
            .WithMessage("Vencimento não pode ser uma data no passado.");
        RuleFor(x => x.MembroId).Null().When(x => x.AplicarATodosOsMembros)
            .WithMessage("membroId não pode ser informado quando aplicarATodosOsMembros é true.");
        RuleFor(x => x.MembroId).NotNull().When(x => !x.AplicarATodosOsMembros)
            .WithMessage("membroId é obrigatório quando aplicarATodosOsMembros é false.");
        RuleFor(x => x.IdempotencyKey).Must(x => !string.IsNullOrWhiteSpace(x)).When(x => x.AplicarATodosOsMembros)
            .WithMessage("Informe o header Idempotency-Key ao aplicar a todos os membros.");
    }
}
