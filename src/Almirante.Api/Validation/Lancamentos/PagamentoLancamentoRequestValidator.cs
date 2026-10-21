using Almirante.Api.Dtos;
using Almirante.Api.Services;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

/// <summary>Regras comuns ao registro e à correção de pagamento; o teto pelo valor do lançamento é verificado no serviço.</summary>
public abstract class PagamentoLancamentoRequestValidator<T> : AbstractValidator<T> where T : PagamentoLancamentoRequest
{
    protected PagamentoLancamentoRequestValidator(DataCivil dataCivil)
    {
        RuleFor(x => x.DataPagamento).NotNull().WithMessage("dataPagamento é obrigatória (yyyy-MM-dd).");
        RuleFor(x => x.DataPagamento!.Value)
            .LessThanOrEqualTo(_ => dataCivil.Hoje())
            .WithName("dataPagamento")
            .WithMessage("dataPagamento não pode ser uma data futura.")
            .When(x => x.DataPagamento.HasValue);

        RuleFor(x => x.ValorPago).NotNull().WithMessage("valorPago é obrigatório.");
        RuleFor(x => x.ValorPago!.Value)
            .GreaterThan(0).WithMessage("valorPago deve ser maior que zero.")
            .LessThanOrEqualTo(EventoRegras.ValorMaximo).WithMessage("valorPago excede o máximo permitido.")
            .Must(v => decimal.Round(v, 2) == v).WithMessage("valorPago deve ter no máximo 2 casas decimais.")
            .WithName("valorPago")
            .When(x => x.ValorPago.HasValue);
    }
}

public sealed class RegistrarPagamentoRequestValidator(DataCivil dataCivil)
    : PagamentoLancamentoRequestValidator<RegistrarPagamentoRequest>(dataCivil);

public sealed class AtualizarPagamentoRequestValidator(DataCivil dataCivil)
    : PagamentoLancamentoRequestValidator<AtualizarPagamentoRequest>(dataCivil);
