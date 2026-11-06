using Almirante.Api.Options;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

public sealed class DataCivil(TimeProvider clock, IOptions<ClubeOptions> options)
{
    private readonly TimeZoneInfo _fuso = TimeZoneInfo.FindSystemTimeZoneById(options.Value.FusoHorario.Trim());

    public DateOnly Hoje() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _fuso).DateTime);

    /// <summary>Quanto falta, no relógio de parede do clube, para a próxima meia-noite: o instante em que <see cref="Hoje"/> muda.</summary>
    public TimeSpan AteAViradaDoDia()
    {
        var agora = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _fuso).DateTime;
        return agora.Date.AddDays(1) - agora;
    }
}
