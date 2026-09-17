using Almirante.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

// Remove famílias de refresh (sessão + tokens, via cascade) somente depois do prazo absoluto mais
// uma margem: até lá os hashes consumidos precisam existir para detectar reuso. Lotes pequenos e
// DELETE por id tornam a execução simultânea em várias réplicas segura e idempotente.
public sealed class AuthSessionCleanup(AlmiranteDbContext db, TimeProvider clock, ILogger<AuthSessionCleanup> logger)
{
    public const int DefaultBatchSize = 500;
    public static readonly TimeSpan RetentionAfterExpiry = TimeSpan.FromHours(1);
    private const int MaxBatchesPerRun = 200;

    public async Task<int> RemoveExpiredAsync(CancellationToken ct, int batchSize = DefaultBatchSize)
    {
        if (!db.Database.IsRelational()) return 0;

        var cutoff = clock.GetUtcNow().UtcDateTime - RetentionAfterExpiry;
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var ids = await db.AuthSessions.AsNoTracking().Where(x => x.AbsoluteExpiresAtUtc < cutoff)
                .OrderBy(x => x.AbsoluteExpiresAtUtc).Select(x => x.Id).Take(batchSize).ToListAsync(ct);
            if (ids.Count == 0) break;
            total += await db.AuthSessions.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
            if (ids.Count < batchSize) break;
        }

        if (total > 0) logger.LogInformation("Removidas {Count} sessões de autenticação expiradas.", total);
        return total;
    }
}

public sealed class AuthSessionCleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AuthSessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AuthSessionCleanup>().RemoveExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Falha na limpeza de sessões expiradas."); }
            await Task.Delay(TimeSpan.FromHours(6), clock, stoppingToken);
        }
    }
}
