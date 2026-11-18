using Almirante.Api.Dtos;
using Almirante.Api.Hubs;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Notificacoes")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class NotificacoesController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista o histórico de notificações do usuário autenticado, lidas e não lidas.")]
    [EndpointDescription("Somente do usuário do token (não há parâmetro de usuário). filtro: Todas (padrão), Lidas ou NaoLidas; " +
        "lidaEmUtc é nula enquanto a notificação não foi lida. Mesma notificação, ordem e cursor de /Pendentes: criadaEmUtc decrescente, " +
        "depois id decrescente; pageSize de 1 a 50 (padrão 20); envie o proximoCursor da resposta anterior em cursor, com o mesmo filtro; " +
        "nulo indica a última página. naoLidas é o total de não lidas da caixa inteira, independente da página e do filtro. " +
        "Sem notificações: items: [] e naoLidas: 0. A consulta não marca nada como lido.")]
    [ProducesResponseType<HistoricoNotificacoesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListarHistorico(
        [FromQuery] FiltroLeituraNotificacoes filtro = FiltroLeituraNotificacoes.Todas,
        [FromQuery] int pageSize = NotificacoesService.PageSizePadrao,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        return Ok(await mediator.Send(new ListarHistoricoNotificacoesQuery(usuarioId, filtro, pageSize, cursor), cancellationToken));
    }

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

    [HttpPost("Leitura/Todas")]
    [EndpointSummary("Marca como lidas, de uma vez, todas as notificações pendentes do usuário autenticado.")]
    [EndpointDescription("Ação do botão Limpar. Somente do usuário do token (sem corpo e sem parâmetro de usuário). Abrange todas as " +
        "pendências criadas até limiteUtc, o instante em que a ação começou, em qualquer página; as marcadas recebem limiteUtc em lidaEmUtc. " +
        "Notificações criadas depois do limite continuam pendentes e entram em naoLidas. O 200 só é enviado depois da gravação, com marcadas " +
        "(quantas esta chamada marcou) e naoLidas (total depois dela). Repetir é seguro: as já lidas mantêm a data original e marcadas vem 0. " +
        "As notificações continuam no histórico (GET /api/Notificacoes). As demais conexões do usuário recebem um evento NotificacaoLida por " +
        "notificação marcada pelo hub. A leitura individual (POST /{id}/Leitura) continua igual.")]
    [ProducesResponseType<LeituraTodasNotificacoesResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarcarTodasComoLidas(CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        return Ok(await mediator.Send(new MarcarTodasNotificacoesLidasCommand(usuarioId), cancellationToken));
    }

    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });
}
