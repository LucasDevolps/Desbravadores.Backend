using System.Threading.RateLimiting;
using Almirante.Api.Options;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

// Limites do envio de foto (issue #116), aplicados no controller DEPOIS da autenticação (o middleware de rate limit roda
// antes dela, sem saber quem é o usuário) e ANTES de ler o corpo:
//   - por usuário: no máximo UploadsPerUserPerWindow envios a cada UploadWindowMinutes (contam também os recusados: o
//     custo de receber o arquivo já aconteceu);
//   - global: no máximo MaxConcurrentProcessing decodificações ao mesmo tempo (CPU e memória), com fila curta.
// Em memória, por instância da API, como o rate limit do login.
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
