using System.Globalization;
using Almirante.Api.Dtos;
using FluentValidation;

namespace Almirante.Api.Validation.Dashboard;

public sealed class GetDashboardQueryValidator : AbstractValidator<GetDashboardQuery>
{
    public const string Formato = "yyyy-MM-dd";

    /// <summary>Mesmo limite da listagem de eventos.</summary>
    public const int PeriodoMaximoDias = Eventos.ListEventosQueryValidator.PeriodoMaximoDias;

    public GetDashboardQueryValidator()
    {
        RuleFor(x => x.DataInicial).Must(SerDataValida).WithName("dataInicial")
            .WithMessage($"dataInicial deve ser uma data válida no formato {Formato}.").When(x => x.DataInicial is not null);
        RuleFor(x => x.DataFinal).Must(SerDataValida).WithName("dataFinal")
            .WithMessage($"dataFinal deve ser uma data válida no formato {Formato}.").When(x => x.DataFinal is not null);

        RuleFor(x => x.DataFinal).NotNull().WithName("dataFinal")
            .WithMessage($"Informe dataFinal junto com dataInicial ({Formato}).").When(x => x.DataInicial is not null);
        RuleFor(x => x.DataInicial).NotNull().WithName("dataInicial")
            .WithMessage($"Informe dataInicial junto com dataFinal ({Formato}).").When(x => x.DataFinal is not null);

        When(x => TentarLer(x.DataInicial, out _) && TentarLer(x.DataFinal, out _), () =>
        {
            RuleFor(x => x).Must(x => Ler(x.DataInicial!) <= Ler(x.DataFinal!))
                .WithName("dataInicial").WithMessage("dataInicial não pode ser posterior a dataFinal.");
            RuleFor(x => x).Must(x => Ler(x.DataFinal!).DayNumber - Ler(x.DataInicial!).DayNumber < PeriodoMaximoDias)
                .WithName("dataFinal").WithMessage($"O período consultado não pode ultrapassar {PeriodoMaximoDias} dias.")
                .When(x => Ler(x.DataInicial!) <= Ler(x.DataFinal!));
        });
    }

    public static bool TentarLer(string? texto, out DateOnly data) =>
        DateOnly.TryParseExact(texto, Formato, CultureInfo.InvariantCulture, DateTimeStyles.None, out data);

    public static DateOnly Ler(string texto) => DateOnly.ParseExact(texto, Formato, CultureInfo.InvariantCulture);

    private static bool SerDataValida(string? texto) => TentarLer(texto, out _);
}
