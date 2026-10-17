using Almirante.Api.Data;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

public sealed class FotosCleanupService(IServiceScopeFactory scopes, IFileStorage armazenamento, IOptions<MediaOptions> options,
    TimeProvider clock, ILogger<FotosCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromHours(24);
    public static readonly TimeSpan IdadeMinimaOrfa = TimeSpan.FromHours(24);
    public static readonly TimeSpan IdadeMinimaIncompleto = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Storage.LimpezaHabilitada)
        {
            return;
        }

        while (true)
        {
            try
            {
                await LimparAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na faxina de fotos.");
            }

            await Task.Delay(Intervalo, clock, stoppingToken);
        }
    }

    public async Task<(int Incompletos, int Orfas)> LimparAsync(CancellationToken ct)
    {
        var agora = clock.GetUtcNow().UtcDateTime;
        var incompletos = await armazenamento.PurgeIncompleteAsync(agora - IdadeMinimaIncompleto, ct);
        var candidatas = (await armazenamento.ListAsync(ct)).Where(a => a.ModificadoEmUtc < agora - IdadeMinimaOrfa).ToList();

        var orfas = 0;
        if (candidatas.Count > 0)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
            var referenciadas = (await db.Usuarios.AsNoTracking().Where(u => u.FotoCaminho != null).Select(u => u.FotoCaminho!).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var orfa in candidatas.Where(a => !referenciadas.Contains(a.Chave)))
            {
                await armazenamento.DeleteAsync(orfa.Chave, ct);
                orfas++;
            }
        }

        if (incompletos + orfas > 0)
        {
            logger.LogInformation("Faxina de fotos: {Incompletos} gravação(ões) interrompida(s) e {Orfas} foto(s) órfã(s) removidas.", incompletos, orfas);
        }

        return (incompletos, orfas);
    }
}
