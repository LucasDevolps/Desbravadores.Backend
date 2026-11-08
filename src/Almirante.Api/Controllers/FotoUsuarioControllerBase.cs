using System.Globalization;
using System.Threading.RateLimiting;
using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Almirante.Api.Controllers;

/// <summary>
/// Envio, download e remoção de foto de perfil, iguais para a foto de terceiros (/api/Usuarios/{id}/foto, cadastros) e a própria
/// (/api/Me/foto, autoatendimento): mesmos limites, validação, armazenamento, auditoria e cabeçalhos.
/// </summary>
public abstract class FotoUsuarioControllerBase(ISender mediator, LimitadorEnvioFotos limitador, IOptions<MediaOptions> media, ILogger logger)
    : ControllerBase
{
    protected ISender Mediator => mediator;

    protected async Task<IActionResult> EnviarFotoAsync(Guid alvoId, Guid responsavelId, CancellationToken cancellationToken)
    {
        using (var envio = limitador.RegistrarEnvio(responsavelId))
        {
            if (!envio.IsAcquired)
            {
                return MuitosEnvios(envio, responsavelId, "Muitos envios de foto.", "Aguarde alguns minutos antes de enviar outra foto.");
            }
        }

        using var vaga = await limitador.ReservarProcessamentoAsync(cancellationToken);
        if (!vaga.IsAcquired)
        {
            return MuitosEnvios(vaga, responsavelId, "Processamento de fotos ocupado.", "Muitas fotos sendo processadas agora; tente novamente em instantes.");
        }

        var upload = await LeitorUploadFoto.LerAsync(Request, media.Value.Images.MaxUploadBytes, cancellationToken);
        var request = new EnviarFotoUsuarioRequest(alvoId, upload.Conteudo, upload.Formato, responsavelId, HttpContext.IpDeOrigem(), User.Role());
        return Ok(await mediator.Send(request, cancellationToken));
    }

    protected async Task<IActionResult> ObterFotoAsync(Guid alvoId, CancellationToken cancellationToken)
    {
        var foto = await mediator.Send(new ObterFotoUsuarioQuery(alvoId), cancellationToken);
        if (foto is null)
        {
            return NotFound(new ProblemDetails { Title = "Foto não encontrada.", Status = StatusCodes.Status404NotFound });
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.ContentDisposition = "inline; filename=\"foto.webp\"";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return File(foto.Conteudo, "image/webp", lastModified: null, entityTag: new EntityTagHeaderValue(foto.ETag), enableRangeProcessing: false);
    }

    protected async Task<IActionResult> ExcluirFotoAsync(Guid alvoId, Guid responsavelId, CancellationToken cancellationToken)
    {
        await mediator.Send(new ExcluirFotoUsuarioRequest(alvoId, responsavelId, HttpContext.IpDeOrigem(), User.Role()), cancellationToken);
        return NoContent();
    }

    protected UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });

    private ObjectResult MuitosEnvios(RateLimitLease recusa, Guid usuarioId, string titulo, string detalhe)
    {
        var segundos = recusa.TryGetMetadata(MetadataName.RetryAfter, out var espera) ? Math.Max(1, (int)Math.Ceiling(espera.TotalSeconds)) : 5;
        logger.LogWarning("Envio de foto recusado (429) {Titulo}: usuário {UsuarioId}.", titulo, usuarioId);
        Response.Headers.RetryAfter = segundos.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new ProblemDetails { Title = titulo, Detail = detalhe, Status = StatusCodes.Status429TooManyRequests });
    }
}
