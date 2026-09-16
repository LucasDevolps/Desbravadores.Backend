using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

public sealed class CreateLancamentoRequestValidator : AbstractValidator<CreateLancamentoRequest>
{
    public CreateLancamentoRequestValidator()
    {
        RuleFor(x => x.MembroNome).NotEmpty();
        RuleFor(x => x.Tipo).LancamentoTipoValido();
        RuleFor(x => x.Categoria).LancamentoCategoriaValida();
        RuleFor(x => x.Status).LancamentoStatusValido();
        RuleFor(x => x.Valor).LancamentoValorValido();
        RuleFor(x => x.Vencimento).LancamentoVencimentoValido();
    }
}
