using System.Globalization;
using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Lancamentos;

// Regras de período hoje aplicadas em LancamentosGeraisService.ResolvePeriodo: período deve ser
// um dos valores conhecidos (vazio/"30"/"60"/"90"/"personalizado"), e "personalizado" exige
// dataInicio/dataFim no formato yyyy-MM-dd com dataInicio <= dataFim.
public sealed class ListLancamentosGeraisQueryValidator : AbstractValidator<ListLancamentosGeraisQuery>
{
    private static readonly string[] PeriodosConhecidos = ["30", "60", "90", "personalizado"];

    public ListLancamentosGeraisQueryValidator()
    {
        RuleFor(x => x.Periodo)
            .Must(periodo => string.IsNullOrWhiteSpace(periodo) || PeriodosConhecidos.Contains(periodo))
            .WithMessage(x => $"Período inválido: {x.Periodo}.");

        When(x => x.Periodo == "personalizado", () =>
        {
            RuleFor(x => x.DataInicio)
                .NotEmpty()
                .WithMessage("Período personalizado exige dataInicio e dataFim.")
                .Must(SerDataValida)
                .WithMessage("dataInicio inválida. Use o formato yyyy-MM-dd.");

            RuleFor(x => x.DataFim)
                .NotEmpty()
                .WithMessage("Período personalizado exige dataInicio e dataFim.")
                .Must(SerDataValida)
                .WithMessage("dataFim inválida. Use o formato yyyy-MM-dd.");

            RuleFor(x => x)
                .Must(x => !SerDataValida(x.DataInicio) || !SerDataValida(x.DataFim) ||
                           ParseData(x.DataInicio!) <= ParseData(x.DataFim!))
                .WithMessage("dataInicio não pode ser posterior a dataFim.")
                .WithName("dataInicio");
        });
    }

    private static bool SerDataValida(string? data) =>
        data is not null &&
        DateOnly.TryParseExact(data, LancamentoValidationRules.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static DateOnly ParseData(string data) =>
        DateOnly.ParseExact(data, LancamentoValidationRules.DateFormat, CultureInfo.InvariantCulture);
}
