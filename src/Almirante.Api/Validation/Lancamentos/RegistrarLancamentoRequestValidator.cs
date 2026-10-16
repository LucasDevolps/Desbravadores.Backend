using Almirante.Api.Dtos;
using FluentValidation;
namespace Almirante.Api.Validation.Lancamentos;
public sealed class RegistrarLancamentoRequestValidator : AbstractValidator<RegistrarLancamentoRequest>
{
    public RegistrarLancamentoRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Finalidade).LancamentoFinalidadeValida();
        RuleFor(x => x.Categoria).IsInEnum();
        RuleFor(x => x.TipoFluxo).IsInEnum();
        RuleFor(x => x.Valor).LancamentoValorValido();
        RuleFor(x => x.Vencimento).NotEmpty().WithMessage("Vencimento é obrigatório.")
            // A API persiste/audita em UTC. Usar a mesma referência aqui evita que "hoje"
            // oscile conforme o fuso horário da máquina que hospeda a aplicação ou executa CI.
            .GreaterThanOrEqualTo(_ => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            .WithMessage("Vencimento não pode ser uma data no passado.");
        RuleFor(x => x.MembroId).Null().When(x => x.AplicarATodosOsMembros)
            .WithMessage("membroId não pode ser informado quando aplicarATodosOsMembros é true.");
        RuleFor(x => x.MembroId).NotNull().When(x => !x.AplicarATodosOsMembros)
            .WithMessage("membroId é obrigatório quando aplicarATodosOsMembros é false.");
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
        RuleFor(x => x.IdempotencyKey).Must(x => !string.IsNullOrWhiteSpace(x)).When(x => x.AplicarATodosOsMembros)
            .WithMessage("Informe o header Idempotency-Key ao aplicar a todos os membros.");
    }
}
