using Microsoft.Extensions.Options;

namespace Almirante.Api.Options;

public sealed class ClubeOptions
{
    public const string SectionName = "Clube";

    public string FusoHorario { get; set; } = "America/Sao_Paulo";
}

public sealed class ClubeOptionsValidator : IValidateOptions<ClubeOptions>
{
    public ValidateOptionsResult Validate(string? name, ClubeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FusoHorario))
        {
            return ValidateOptionsResult.Fail("Clube:FusoHorario é obrigatório (ex.: America/Sao_Paulo).");
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(options.FusoHorario.Trim(), out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Clube:FusoHorario '{options.FusoHorario}' não existe neste sistema (instale tzdata/ICU ou corrija o valor).");
    }
}
