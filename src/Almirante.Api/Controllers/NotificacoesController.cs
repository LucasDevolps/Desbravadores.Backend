using Almirante.Api.Dtos;
using Almirante.Api.Hubs;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>Notificações do próprio usuário autenticado (qualquer cargo ativo, inclusive desbravadores).</summary>
[ApiController]
[Route("api/Notificacoes")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class NotificacoesController(ISender mediator) : ControllerBase
{
    [HttpGet("Pendentes")]
    [EndpointSummary("Lista as notificações não lidas do usuário autenticado.")]
    [EndpointDescription("Somente do usuário do token (não há parâmetro de usuário). Ordem: criadaEmUtc decrescente, depois id decrescente. " +
        "pageSize de 1 a 50 (padrão 20). Paginação por cursor: envie o proximoCursor da resposta " +
        "anterior em cursor; nulo indica a última página. naoLidas é o total de pendências, independente da página. Sem pendências: items: [] e naoLidas: 0. " +
        "A listagem não marca nada como lido. Avisos em tempo real: hub SignalR " + NotificacoesHub.Caminho + " (ver docs/notificacoes.md).")]
    [ProducesResponseType<NotificacoesPendentesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListarPendentes(
        [FromQuery] int pageSize = NotificacoesService.PageSizePadrao,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        return Ok(await mediator.Send(new ListarNotificacoesPendentesQuery(usuarioId, pageSize, cursor), cancellationToken));
    }

    [HttpPost("{id:guid}/Leitura")]
    [ValidateAntiForgeryToken]
    [EndpointSummary("Marca uma notificação do usuário autenticado como lida.")]
    [EndpointDescription("Chame somente quando o usuário abrir a notificação. Grava a data/hora (UTC) da primeira leitura e devolve a notificação " +
        "e o novo total de não lidas; o 200 só é enviado depois da gravação. Idempotente: repetir devolve 200 com a mesma lidaEmUtc original. " +
        "As demais conexões do usuário recebem o evento NotificacaoLida pelo hub. 404 para notificação inexistente ou de outro usuário " +
        "(mesma resposta nos dois casos); a leitura nunca altera notificações de outros destinatários.")]
    [ProducesResponseType<LeituraNotificacaoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarcarComoLida(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        var leitura = await mediator.Send(new MarcarNotificacaoLidaCommand(usuarioId, id), cancellationToken);
        return leitura is null
            ? NotFound(new ProblemDetails { Title = "Notificação não encontrada.", Status = StatusCodes.Status404NotFound })
            : Ok(leitura);
    }

    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });
}
