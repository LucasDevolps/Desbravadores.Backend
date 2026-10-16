using Almirante.Api.Options;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

// "Hoje" civil do clube (Clube:FusoHorario) para regras de idade. Diferente da data UTC usada no restante da API: entre
// 21h e 23h59 (UTC-3) o UTC já está no dia seguinte, o que trataria como maior de idade, por três horas, quem só
// completa 18 anos no dia seguinte.
public sealed class DataCivil(TimeProvider clock, IOptions<ClubeOptions> options)
{
    private readonly TimeZoneInfo _fuso = TimeZoneInfo.FindSystemTimeZoneById(options.Value.FusoHorario.Trim());

    public DateOnly Hoje() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _fuso).DateTime);
}
