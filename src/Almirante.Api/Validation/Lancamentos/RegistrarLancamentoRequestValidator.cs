using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
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
            .GreaterThanOrEqualTo(_ => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            .WithMessage("Vencimento não pode ser uma data no passado.");
        RuleFor(x => x.MembroId).Null().When(x => x.AplicarATodosOsMembros)
            .WithMessage("membroId não pode ser informado quando aplicarATodosOsMembros é true.");
        RuleFor(x => x.MembroId).NotNull().When(x => !x.AplicarATodosOsMembros)
            .WithMessage("membroId é obrigatório quando aplicarATodosOsMembros é false.");
        RuleFor(x => x.Descricao!).CabeEmVarchar("descricao", 500).When(x => x.Descricao is not null);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
        RuleFor(x => x.IdempotencyKey).Must(x => UsuariosService.SomenteAsciiImprimivel(x!.Trim())).When(x => x.IdempotencyKey is not null)
            .WithMessage("Idempotency-Key deve conter apenas caracteres ASCII.");
        RuleFor(x => x.IdempotencyKey).Must(x => !string.IsNullOrWhiteSpace(x)).When(x => x.AplicarATodosOsMembros)
            .WithMessage("Informe o header Idempotency-Key ao aplicar a todos os membros.");
    }
}
