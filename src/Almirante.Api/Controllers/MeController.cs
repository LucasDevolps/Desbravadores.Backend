using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Controllers;

/// <summary>
/// Autoatendimento: dados, foto e eventos do usuário do token (qualquer cargo ativo, inclusive desbravadores). Não há id de usuário
/// na rota nem no corpo; nada aqui dá acesso a outro usuário nem a operações administrativas.
/// </summary>
[ApiController]
[Route("api/Me")]
[Authorize(Policy = Policies.Autoatendimento)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class MeController(ISender mediator, LimitadorEnvioFotos limitador, IOptions<MediaOptions> media, ILogger<MeController> logger)
    : FotoUsuarioControllerBase(mediator, limitador, media, logger)
{
    [HttpGet]
    [EndpointSummary("Consulta os próprios dados cadastrais.")]
    [EndpointDescription("Usuário identificado só pelo token. Mesmos campos pessoais de GET /api/Usuarios (cpf, documento, contato, saúde, " +
        "responsável e endereço), com cargo resumido (id, nome, role) e fotoUrl apontando para /api/Me/foto. Sem situação, auditoria ou senha.")]
    [ProducesResponseType<MeusDadosDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeusDadosDto>> Obter(CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(new ObterMeusDadosQuery(User.UsuarioId()), cancellationToken));

    [HttpPut]
    [EndpointSummary("Atualiza os próprios dados pessoais (estado anterior auditado em _usuarios_hist).")]
    [EndpointDescription("Aceita somente cpf, dataNascimento, telefone, rg, ehCin, dataExpiracao, tipoSanguineo, doencasAlergias, endereco " +
        "e responsavelNome/responsavelEmail/responsavelCpf/responsavelTelefone, com as mesmas regras e a mesma semântica de omissão do " +
        "PUT /api/Usuarios/{id} (omitido mantém; null ou \"\" remove; cpf gravado não pode ser removido; rg exigido sem CIN; responsável " +
        "obrigatório para menor de 18 anos; CPF único; CEP consultado no ViaCEP). Qualquer outra propriedade (nome, email, cargoId, senha, " +
        "ativo, id...) é recusada com 400 e nada é gravado: nome, e-mail e cargo são da secretaria/diretoria; senha não muda por aqui. " +
        "409 CPF de outro usuário; 503 ViaCEP indisponível.")]
    [ProducesResponseType<MeusDadosDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MeusDadosDto>> Atualizar([FromBody] AtualizarMeusDadosRequest request, CancellationToken cancellationToken)
    {
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
        return Ok(await Mediator.Send(request, cancellationToken));
    }

    [HttpGet("foto")]
    [EndpointSummary("Baixa a própria foto de perfil (WebP).")]
    [EndpointDescription("Mesmo comportamento de GET /api/Usuarios/{id}/foto (Bearer, ETag/304, Cache-Control private, no-cache). 404 sem foto.")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "image/webp")]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ObterFoto(CancellationToken cancellationToken) => ObterFotoAsync(User.UsuarioId(), cancellationToken);

    [HttpPut("foto")]
    [DisableFormValueModelBinding]
    [Consumes("multipart/form-data")]
    [EndpointSummary("Envia ou substitui a própria foto de perfil (troca auditada em _usuarios_hist).")]
    [EndpointDescription("Mesmas regras, limites e respostas de PUT /api/Usuarios/{id}/foto: multipart com um único campo 'arquivo' (JPEG, PNG " +
        "ou WebP), regravado em WebP sem metadados. 400 multipart inválido, 413 acima do limite, 415 formato não suportado, 422 imagem " +
        "inválida, 429 excesso de envios, 503 armazenamento indisponível.")]
    [ProducesResponseType<MeusDadosDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EnviarFoto(CancellationToken cancellationToken)
    {
        var eu = User.UsuarioId();
        var resultado = await EnviarFotoAsync(eu, eu, cancellationToken);
        // Responde como GET /api/Me (fotoUrl em /api/Me/foto), não com o item administrativo de /api/Usuarios.
        return resultado is OkObjectResult ? Ok(await Mediator.Send(new ObterMeusDadosQuery(eu), cancellationToken)) : resultado;
    }

    [HttpDelete("foto")]
    [EndpointSummary("Remove a própria foto de perfil (remoção auditada em _usuarios_hist; o arquivo é apagado).")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ExcluirFoto(CancellationToken cancellationToken)
    {
        var eu = User.UsuarioId();
        return ExcluirFotoAsync(eu, eu, cancellationToken);
    }

    [HttpGet("Eventos")]
    [EndpointSummary("Lista os eventos dos quais o usuário autenticado participa.")]
    [EndpointDescription("Somente cadastros ativos em que o usuário é participante ativo, passados e futuros, em ordem de data. Cada item traz data, " +
        "local, composição do valor por membro e a própria cobrança (valor, vencimento, status); nunca os demais participantes, o total ou os " +
        "lançamentos de outros. Sem eventos: items: [].")]
    [ProducesResponseType<MeusEventosResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeusEventosResponse>> ListarEventos(CancellationToken cancellationToken) =>
        Ok(await Mediator.Send(new ListarMeusEventosQuery(User.UsuarioId()), cancellationToken));

    [HttpGet("Eventos/{id:guid}")]
    [EndpointSummary("Consulta um evento do qual o usuário autenticado participa (o eventoId das notificações).")]
    [EndpointDescription("Mesma representação de GET /api/Me/Eventos. 404, com a mesma resposta, para evento inexistente, excluído, de que o " +
        "usuário não participa ou do qual foi removido. Eventos passados continuam consultáveis.")]
    [ProducesResponseType<EventoParticipanteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventoParticipanteDto>> ObterEvento(Guid id, CancellationToken cancellationToken)
    {
        var evento = await Mediator.Send(new ObterMeuEventoQuery(User.UsuarioId(), id), cancellationToken);
        return evento is null
            ? NotFound(new ProblemDetails { Title = "Evento não encontrado.", Status = StatusCodes.Status404NotFound })
            : Ok(evento);
    }
}
