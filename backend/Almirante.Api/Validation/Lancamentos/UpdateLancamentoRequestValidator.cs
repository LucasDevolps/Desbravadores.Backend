using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

// Todos os campos são opcionais (atualização parcial) — cada regra só roda quando o campo
// correspondente é de fato enviado, replicando o comportamento anterior de LancamentosService.UpdateAsync.
public sealed class UpdateLancamentoRequestValidator : AbstractValidator<UpdateLancamentoRequest>
{
    public UpdateLancamentoRequestValidator()
    {
        RuleFor(x => x.Tipo!).LancamentoTipoValido().When(x => x.Tipo is not null);
        RuleFor(x => x.Categoria!).LancamentoCategoriaValida().When(x => x.Categoria is not null);
        RuleFor(x => x.Status!).LancamentoStatusValido().When(x => x.Status is not null);
        RuleFor(x => x.Valor!.Value).LancamentoValorValido().When(x => x.Valor.HasValue);
        RuleFor(x => x.Vencimento!).LancamentoVencimentoValido().When(x => x.Vencimento is not null);
    }
}
