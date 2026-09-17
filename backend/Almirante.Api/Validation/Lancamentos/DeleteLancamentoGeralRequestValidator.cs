using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

public sealed class DeleteLancamentoGeralRequestValidator : AbstractValidator<DeleteLancamentoGeralRequest>
{
    public DeleteLancamentoGeralRequestValidator()
    {
        RuleFor(x => x.Motivo)
            .Must(motivo => motivo.Trim().Length is > 0 and <= 255)
            .WithMessage("Motivo é obrigatório e deve ter entre 1 e 255 caracteres após remover espaços das extremidades.");
    }
}
