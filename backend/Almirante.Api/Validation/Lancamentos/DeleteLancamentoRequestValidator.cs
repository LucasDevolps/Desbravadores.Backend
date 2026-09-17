using Almirante.Api.Dtos;
using FluentValidation;
namespace Almirante.Api.Validation.Lancamentos;
public sealed class DeleteLancamentoRequestValidator : AbstractValidator<DeleteLancamentoRequest>
{
    public DeleteLancamentoRequestValidator() => RuleFor(x=>x.Motivo).NotEmpty().MaximumLength(255);
}
