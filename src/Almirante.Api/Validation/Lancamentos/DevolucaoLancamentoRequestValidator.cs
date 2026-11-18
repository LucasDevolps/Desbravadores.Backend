using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

/// <summary>
/// Baixa de devolução de evento (issue #209). O teto pelo valor esperado e a justificativa obrigatória quando o valor difere dependem da
/// devolução gravada e são conferidos no serviço; aqui ficam a data, o formato do valor e o formato da justificativa informada.
/// </summary>
public abstract class DevolucaoLancamentoRequestValidator<T> : AbstractValidator<T> where T : DevolucaoLancamentoRequest
{
    protected DevolucaoLancamentoRequestValidator(DataCivil dataCivil)
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

        RuleFor(x => DevolucaoRegras.NormalizarJustificativa(x.Justificativa)!)
            .Cascade(CascadeMode.Stop)
            .Must(DevolucaoRegras.JustificativaValida)
            .WithMessage($"justificativa deve ter pelo menos {DevolucaoRegras.JustificativaMinimo} caracteres, sem contar os espaços.")
            .CabeEmVarchar("justificativa", AlmiranteDbContext.DevolucaoJustificativaMaximo)
            .WithName("justificativa")
            .OverridePropertyName("justificativa")
            .When(x => DevolucaoRegras.NormalizarJustificativa(x.Justificativa) is not null);
    }
}

public sealed class RegistrarDevolucaoRequestValidator(DataCivil dataCivil)
    : DevolucaoLancamentoRequestValidator<RegistrarDevolucaoRequest>(dataCivil);

public sealed class AtualizarDevolucaoRequestValidator(DataCivil dataCivil)
    : DevolucaoLancamentoRequestValidator<AtualizarDevolucaoRequest>(dataCivil);
