using Almirante.Api.Dtos;
using Almirante.Api.Validation.Usuarios;
using FluentValidation;
namespace Almirante.Api.Validation.Lancamentos;
public sealed class DeleteLancamentoRequestValidator : AbstractValidator<DeleteLancamentoRequest>
{
    public DeleteLancamentoRequestValidator() => RuleFor(x=>x.Motivo).Cascade(CascadeMode.Stop).NotEmpty().CabeEmVarchar("motivo", 255);
}
