using Almirante.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Almirante.Api.Infrastructure;

// Substitui o health check do Aspire (que abre conexão só com a connection string, sem a credencial
// injetada em tempo de execução pelo AppDbCredentialInterceptor).
public sealed class DbConnectivityHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Sem conexão com o banco de dados.");
    }
}
