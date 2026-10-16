using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

/// <summary>Rotaciona periodicamente a senha do usuário SQL da aplicação (a primeira rotação ocorre no startup).</summary>
public sealed class DbCredentialRotationService(
    DbCredentialManager manager,
    IOptions<DbCredentialOptions> options,
    TimeProvider clock,
    ILogger<DbCredentialRotationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.RotationHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await manager.RotateAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // Falha (ex.: SQL Server indisponível): a senha anterior continua válida; tenta de novo no próximo ciclo.
            catch (Exception exception) { logger.LogError(exception, "Falha ao rotacionar a senha do usuário SQL da aplicação."); }
        }
    }
}
