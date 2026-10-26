using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Infrastructure;

/// <summary>429 em Problem Details com Retry-After, para limites aplicados dentro das actions (depois da autenticação).</summary>
public static class RespostaMuitasTentativas
{
    public static ObjectResult Criar(HttpResponse response, RateLimitLease recusa, string titulo, string detalhe)
    {
        var segundos = recusa.TryGetMetadata(MetadataName.RetryAfter, out var espera) ? Math.Max(1, (int)Math.Ceiling(espera.TotalSeconds)) : 60;
        response.Headers.RetryAfter = segundos.ToString(CultureInfo.InvariantCulture);
        return new ObjectResult(new ProblemDetails { Title = titulo, Detail = detalhe, Status = StatusCodes.Status429TooManyRequests })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentTypes = { "application/problem+json" },
        };
    }
}
