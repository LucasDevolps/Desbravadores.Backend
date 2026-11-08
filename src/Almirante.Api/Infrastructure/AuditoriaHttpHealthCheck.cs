using Almirante.Api.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Almirante.Api.Infrastructure;

/// <summary>
/// Sinaliza (Degraded, que /health ainda responde com 200) quando a auditoria HTTP perdeu garantias: diário em disco indisponível, banco
/// recusando a gravação há mais de um minuto ou registros descartados. O texto vai só para quem consulta o resultado detalhado; o
/// endpoint /health publica apenas o status.
/// </summary>
public sealed class AuditoriaHttpHealthCheck(DiarioAuditoriaHttp diario, GravadorAuditoriaHttp gravador, TimeProvider tempo) : IHealthCheck
{
    public static readonly TimeSpan ToleranciaBanco = TimeSpan.FromMinutes(1);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problemas = new List<string>();
        if (!diario.DiscoDisponivel)
        {
            problemas.Add($"diário em disco indisponível ({diario.NaReserva} registros só em memória)");
        }

        if (gravador.FalhandoDesde is { } desde && tempo.GetUtcNow() - desde > ToleranciaBanco)
        {
            problemas.Add($"banco recusando a gravação desde {desde:O}");
        }

        if (diario.Descartados > 0)
        {
            problemas.Add($"{diario.Descartados} registros descartados desde o início");
        }

        return Task.FromResult(problemas.Count == 0
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, string.Join("; ", problemas)));
    }
}
