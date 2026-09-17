using Almirante.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>Remove famílias somente depois do prazo absoluto; execução concorrente é idempotente.</summary>
public sealed class AuthSessionCleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AuthSessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
                if (db.Database.IsRelational())
                {
                    var cutoff = clock.GetUtcNow().UtcDateTime.AddHours(-1);
                    var ids = await db.AuthSessions.Where(x => x.AbsoluteExpiresAtUtc < cutoff)
                        .OrderBy(x => x.AbsoluteExpiresAtUtc).Select(x => x.Id).Take(500).ToListAsync(stoppingToken);
                    var removed = ids.Count == 0 ? 0 : await db.AuthSessions.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(stoppingToken);
                    if (removed > 0) logger.LogInformation("Removidas {Count} sessões de autenticação expiradas.", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Falha na limpeza de sessões expiradas."); }
            await Task.Delay(TimeSpan.FromHours(6), clock, stoppingToken);
        }
    }
}
