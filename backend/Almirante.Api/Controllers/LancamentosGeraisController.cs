using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

// Lançamento geral: cria o mesmo lançamento para todos os usuários elegíveis de uma vez (issue
// #14). Escopo separado do CRUD genérico em LancamentosController — rotas, autorização (só as
// roles em [AutorizarRoles] abaixo, com 401 "ACESSO NEGADO!" tanto para requisições não
// autenticadas quanto para autenticadas sem role permitida — ver
// AcessoNegadoAuthorizationMiddlewareResultHandler) e exclusão (lógica, nunca física) são
// exclusivos deste escopo e não alteram o comportamento de LancamentosController.
[ApiController]
[Route("api/Lancamentos/Geral")]
[AutorizarRoles(Roles.Admin, Roles.Diretor, Roles.DiretorAssociado, Roles.Secretario, Roles.Tesoureiro)]
public sealed class LancamentosGeraisController(ISender mediator) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<LancamentoGeralResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LancamentoGeralResponse>> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateLancamentoGeralRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioSolicitanteId))
        {
            return AcessoNegado();
        }

        request.IdempotencyKey = idempotencyKey?.Trim() ?? string.Empty;
        request.UsuarioSolicitanteId = usuarioSolicitanteId;

        var resultado = await mediator.Send(request, cancellationToken);

        return resultado.Outcome switch
        {
            LancamentoGeralCreateOutcome.Criado => Ok(resultado.Response),
            LancamentoGeralCreateOutcome.Reutilizado => Ok(resultado.Response),
            LancamentoGeralCreateOutcome.ConflitoIdempotencia => Conflict(new ProblemDetails
            {
                Title = "Chave de idempotência já usada com dados diferentes.",
                Detail = "O header Idempotency-Key informado já foi usado em uma operação anterior com tipo, categoria, valor ou vencimento diferentes.",
                Status = StatusCodes.Status409Conflict,
            }),
            _ => throw new InvalidOperationException($"Resultado inesperado: {resultado.Outcome}."),
        };
    }

    [HttpGet]
    [ProducesResponseType<LancamentosGeralListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LancamentosGeralListResponse>> List(
        [FromQuery] string? periodo,
        [FromQuery] string? dataInicio,
        [FromQuery] string? dataFim,
        CancellationToken cancellationToken)
    {
        var response = await mediator.Send(new ListLancamentosGeraisQuery(periodo, dataInicio, dataFim), cancellationToken);
        return Ok(response);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromBody] DeleteLancamentoGeralRequest request,
        CancellationToken cancellationToken)
    {
        // O lançamento excluído é sempre `id` (da rota) — usuarioResponsavelId nunca decide qual
        // linha é apagada, só alimenta a auditoria (quem pediu a exclusão, via SESSION_CONTEXT/
        // trigger, ver LancamentosGeraisService.DeleteAsync).
        request.Id = id;

        if (!User.TentarObterUsuarioId(out var usuarioResponsavelId))
        {
            return AcessoNegado();
        }

        request.UsuarioResponsavelId = usuarioResponsavelId;
        request.IpResponsavel = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

        var outcome = await mediator.Send(request, cancellationToken);

        return outcome switch
        {
            LancamentoGeralDeleteOutcome.Excluido => NoContent(),
            LancamentoGeralDeleteOutcome.NaoEncontrado => NotFound(),
            _ => throw new InvalidOperationException($"Resultado inesperado: {outcome}."),
        };
    }

    private UnauthorizedObjectResult AcessoNegado() => Unauthorized(new ProblemDetails
    {
        Title = "ACESSO NEGADO!",
        Status = StatusCodes.Status401Unauthorized,
    });
}
