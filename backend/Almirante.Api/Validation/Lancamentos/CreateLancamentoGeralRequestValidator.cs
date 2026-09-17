using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

public sealed class CreateLancamentoGeralRequestValidator : AbstractValidator<CreateLancamentoGeralRequest>
{
    public CreateLancamentoGeralRequestValidator()
    {
        RuleFor(x => x.Tipo).LancamentoTipoValido();
        RuleFor(x => x.Categoria).LancamentoCategoriaValida();
        RuleFor(x => x.Valor).LancamentoValorValido();
        RuleFor(x => x.Vencimento).LancamentoVencimentoValido();

        RuleFor(x => x.IdempotencyKey)
            .NotEmpty()
            .WithMessage("Informe o header Idempotency-Key (identificador único da requisição, diferente do token de autenticação).");
    }
}
