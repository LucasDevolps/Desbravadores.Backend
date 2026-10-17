using Almirante.Api.Options;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

public sealed class DataCivil(TimeProvider clock, IOptions<ClubeOptions> options)
{
    private readonly TimeZoneInfo _fuso = TimeZoneInfo.FindSystemTimeZoneById(options.Value.FusoHorario.Trim());

    public DateOnly Hoje() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _fuso).DateTime);
}
