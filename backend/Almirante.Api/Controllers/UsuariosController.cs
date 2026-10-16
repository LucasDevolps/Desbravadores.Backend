using System.Globalization;
using System.Threading.RateLimiting;
using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Almirante.Api.Controllers;

// Controller fino (mesmo desenho de EventosController): autorização, dados que só o servidor fornece (responsável
// das claims, IP da conexão, papel do solicitante) e envio ao MediatR. Nenhuma regra de negócio aqui; na foto, só o
// que é HTTP: limites de envio (429), leitura do multipart e os headers da resposta.
[ApiController]
[Route("api/Usuarios")]
[Authorize(Policy = Policies.GestaoCadastros)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public class UsuariosController(UsuariosService usuariosService, ISender mediator, LimitadorEnvioFotos limitador,
    IOptions<MediaOptions> media, ILogger<UsuariosController> logger) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista os usuários; por padrão, somente os ativos.")]
    [EndpointDescription("includeInactive=true inclui os usuários excluídos logicamente. funcao traz o nome do cargo (ex.: \"Diretor\"). " +
        "cpf (normalizado), dataNascimento (yyyy-MM-dd), telefone e os dados do responsável vêm null quando não cadastrados; " +
        "fotoUrl é a rota autenticada da foto (null sem foto).")]
    [ProducesResponseType<IReadOnlyList<UsuarioListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UsuarioListItemDto>>> List(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var usuarios = await usuariosService.ListAsync(includeInactive, cancellationToken);
        return Ok(usuarios);
    }

    [HttpPost]
    [EndpointSummary("Cadastra um usuário.")]
    [EndpointDescription("E-mail único sem diferenciar maiúsculas/minúsculas (409 se já existir, inclusive entre inativos). " +
        "cpf, dataNascimento (yyyy-MM-dd, não futura) e telefone são opcionais; cpf precisa ser um CPF válido (11 dígitos, com ou sem " +
        "pontos e hífen) e é único entre todos os usuários (409 se já existir); telefone é fixo ou celular brasileiro com DDD, gravado só com dígitos. " +
        "responsavelNome e responsavelEmail são obrigatórios quando dataNascimento indica menos de 18 anos na data de hoje; " +
        "responsavelCpf e responsavelTelefone seguem as mesmas regras de cpf e telefone (sem unicidade). " +
        "cargoId deve ser um cargo ativo; somente um Administrador cadastra outro Administrador (403).")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Registrar([FromBody] RegistrarUsuarioRequest request, CancellationToken cancellationToken)
    {
        request.SolicitanteEhAdmin = User.IsInRole(Roles.Admin);
        var usuario = await mediator.Send(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, usuario);
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Altera nome, e-mail, cargo, dados pessoais e do responsável de um usuário ativo (estado anterior completo auditado em _usuarios_hist).")]
    [EndpointDescription("nome, email e cargoId são obrigatórios e substituídos. cpf, dataNascimento, telefone e os campos do responsável omitidos " +
        "mantêm o valor atual; null (ou \"\" nos textos) remove. A obrigatoriedade do responsável usa a data de nascimento informada ou, se omitida, " +
        "a já gravada (400 se o menor de 18 anos ficar sem nome ou e-mail do responsável). " +
        "409 se o e-mail ou o CPF pertencer a outro usuário ou se o usuário estiver inativo; 404 se inexistente.")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUsuarioRequest request, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.UsuarioResponsavelId = usuarioId;
        request.IpResponsavel = HttpContext.IpDeOrigem();
        request.SolicitanteEhAdmin = User.IsInRole(Roles.Admin);
        return Ok(await mediator.Send(request, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Exclui logicamente (inativa) um usuário (estado anterior auditado em _usuarios_hist).")]
    [EndpointDescription("409 se o usuário estiver vinculado a eventos de hoje em diante (extensão eventos lista todos), se já estiver inativo " +
        "ou se for o próprio solicitante; 404 se inexistente. Eventos passados não impedem a exclusão. A foto, se houver, é mantida.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        await mediator.Send(new DeleteUsuarioRequest(id, usuarioId, HttpContext.IpDeOrigem(), User.IsInRole(Roles.Admin)), cancellationToken);
        return NoContent();
    }

    [HttpPut("{id:guid}/foto")]
    [DisableFormValueModelBinding]
    [Consumes("multipart/form-data")]
    [EndpointSummary("Envia ou substitui a foto de perfil de um usuário ativo (troca auditada em _usuarios_hist).")]
    [EndpointDescription("multipart/form-data com um único campo 'arquivo': JPEG, PNG ou WebP, com extensão, Content-Type e assinatura coincidentes. " +
        "Limites em Media:Images (padrão: até 10 MB, de 64x64 até 25 megapixels). A API decodifica, corrige a orientação, remove todos os metadados " +
        "(EXIF/GPS, XMP, IPTC, ICC) e regrava em WebP de até 1024x1024 e 1 MB; o arquivo original nunca é guardado. Só um Administrador altera a " +
        "foto de um Administrador (403). 400 multipart inválido, 404 usuário inexistente, 409 usuário inativo, 413 acima do limite, " +
        "415 formato ou Content-Encoding não suportado, 422 imagem inválida/fora dos limites, 429 excesso de envios, 503 armazenamento indisponível.")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EnviarFoto(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        // Antes de ler o corpo: o custo de receber e decodificar só acontece dentro dos limites.
        using (var envio = limitador.RegistrarEnvio(usuarioId))
        {
            if (!envio.IsAcquired)
            {
                return MuitosEnvios(envio, usuarioId, "Muitos envios de foto.", "Aguarde alguns minutos antes de enviar outra foto.");
            }
        }

        using var vaga = await limitador.ReservarProcessamentoAsync(cancellationToken);
        if (!vaga.IsAcquired)
        {
            return MuitosEnvios(vaga, usuarioId, "Processamento de fotos ocupado.", "Muitas fotos sendo processadas agora; tente novamente em instantes.");
        }

        var upload = await LeitorUploadFoto.LerAsync(Request, media.Value.Images.MaxUploadBytes, cancellationToken);
        var request = new EnviarFotoUsuarioRequest(id, upload.Conteudo, upload.Formato, usuarioId, HttpContext.IpDeOrigem(), User.IsInRole(Roles.Admin));
        return Ok(await mediator.Send(request, cancellationToken));
    }

    [HttpGet("{id:guid}/foto")]
    [EndpointSummary("Baixa a foto de perfil de um usuário (WebP gerado pela API).")]
    [EndpointDescription("Exige o token (Bearer): no navegador, busque com HttpClient (responseType blob) e use URL.createObjectURL; uma <img src> " +
        "comum não envia o token. ETag muda a cada troca de foto (If-None-Match -> 304); Cache-Control private, no-cache. 404 sem usuário ou sem foto.")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "image/webp")]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ObterFoto(Guid id, CancellationToken cancellationToken)
    {
        var foto = await mediator.Send(new ObterFotoUsuarioQuery(id), cancellationToken);
        if (foto is null)
        {
            return NotFound(new ProblemDetails { Title = "Foto não encontrada.", Status = StatusCodes.Status404NotFound });
        }

        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.ContentDisposition = "inline; filename=\"foto.webp\"";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return File(foto.Conteudo, "image/webp", lastModified: null, entityTag: new EntityTagHeaderValue(foto.ETag), enableRangeProcessing: false);
    }

    [HttpDelete("{id:guid}/foto")]
    [EndpointSummary("Remove a foto de perfil de um usuário (remoção auditada em _usuarios_hist; o arquivo é apagado).")]
    [EndpointDescription("Também para usuário inativo. 404 se o usuário não existir ou não tiver foto; só um Administrador remove a foto de um Administrador (403).")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExcluirFoto(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        await mediator.Send(new ExcluirFotoUsuarioRequest(id, usuarioId, HttpContext.IpDeOrigem(), User.IsInRole(Roles.Admin)), cancellationToken);
        return NoContent();
    }

    // Inalcançável com o pipeline atual (OnTokenValidated exige sub GUID), mantido como defesa.
    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });

    // 429 com Retry-After: a janela por usuário informa quando libera; a fila de processamento, não (5 s).
    private ObjectResult MuitosEnvios(RateLimitLease recusa, Guid usuarioId, string titulo, string detalhe)
    {
        var segundos = recusa.TryGetMetadata(MetadataName.RetryAfter, out var espera) ? Math.Max(1, (int)Math.Ceiling(espera.TotalSeconds)) : 5;
        logger.LogWarning("Envio de foto recusado (429) {Titulo}: usuário {UsuarioId}.", titulo, usuarioId);
        Response.Headers.RetryAfter = segundos.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new ProblemDetails { Title = titulo, Detail = detalhe, Status = StatusCodes.Status429TooManyRequests });
    }
}
