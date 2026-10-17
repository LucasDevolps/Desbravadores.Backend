using System.Threading.RateLimiting;
using Almirante.Api.Options;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

public sealed class LimitadorEnvioFotos : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _porUsuario;
    private readonly ConcurrencyLimiter _processamento;

    public LimitadorEnvioFotos(IOptions<MediaOptions> options)
    {
        var limites = options.Value.Images;
        _porUsuario = PartitionedRateLimiter.Create<Guid, Guid>(usuarioId => RateLimitPartition.GetFixedWindowLimiter(usuarioId,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limites.UploadsPerUserPerWindow,
                Window = TimeSpan.FromMinutes(limites.UploadWindowMinutes),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
        _processamento = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = limites.MaxConcurrentProcessing,
            QueueLimit = limites.MaxConcurrentProcessing * 2,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    public RateLimitLease RegistrarEnvio(Guid usuarioId) => _porUsuario.AttemptAcquire(usuarioId);

    public ValueTask<RateLimitLease> ReservarProcessamentoAsync(CancellationToken ct) => _processamento.AcquireAsync(1, ct);

    public void Dispose()
    {
        _porUsuario.Dispose();
        _processamento.Dispose();
    }
}
