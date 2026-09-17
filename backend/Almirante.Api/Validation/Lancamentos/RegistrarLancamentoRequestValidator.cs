using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

public sealed class RegistrarLancamentoRequestValidator : AbstractValidator<RegistrarLancamentoRequest>
{
    public RegistrarLancamentoRequestValidator()
    {
        RuleFor(x => x.Tipo).LancamentoTipoValido();
        RuleFor(x => x.Categoria).LancamentoCategoriaValida();
        RuleFor(x => x.Status).LancamentoStatusValido();
        RuleFor(x => x.Valor).LancamentoValorValido();
        RuleFor(x => x.Vencimento).LancamentoVencimentoValido();

        RuleFor(x => x.MembroId)
            .Null()
            .When(x => x.AplicarATodosOsMembros)
            .WithMessage("membroId não pode ser informado quando aplicarATodosOsMembros é true.");

        RuleFor(x => x.MembroNome)
            .NotEmpty()
            .When(x => !x.AplicarATodosOsMembros)
            .WithMessage("membroNome é obrigatório quando aplicarATodosOsMembros é false (nome do membro, do doador/contribuinte, ou descrição da despesa).");

        RuleFor(x => x.IdempotencyKey)
            .Must(key => !string.IsNullOrWhiteSpace(key))
            .When(x => x.AplicarATodosOsMembros)
            .WithMessage("Informe o header Idempotency-Key (identificador único da requisição, diferente do token de autenticação) ao aplicar a todos os membros.");
    }
}
