using Microsoft.Extensions.Options;

namespace Almirante.Api.Options;

// Dados do clube usados por regras de calendário (ex.: maioridade do responsável, ver DataCivil).
public sealed class ClubeOptions
{
    public const string SectionName = "Clube";

    // Identificador IANA (Windows resolve via ICU, Linux via tzdata). Validado no startup: um fuso inexistente
    // derrubaria a primeira requisição de cadastro em vez do deploy.
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
