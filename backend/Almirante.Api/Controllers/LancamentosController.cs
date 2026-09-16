using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Lancamentos")]
[Authorize]
public sealed class LancamentosController(ISender mediator) : ControllerBase
{

    [HttpGet]
    [AutorizarRoles(Roles.Admin, Roles.Diretor, Roles.DiretorAssociado, Roles.Secretario, Roles.Tesoureiro)]
    [ProducesResponseType<LancamentosResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LancamentosResponse>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? tipo = null,
        [FromQuery] string? data = null,
        CancellationToken cancellationToken = default)
    {
        var response = await mediator.Send(
            new ListLancamentosQuery(page, pageSize, search, status, tipo, data), cancellationToken
        );
        return Ok(response);
    }

    [HttpPost]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LancamentoDto>> Create([FromBody] CreateLancamentoRequest request, CancellationToken cancellationToken)
    {
        var created = await mediator.Send(request, cancellationToken);
        return CreatedAtAction(nameof(List), new { }, created);
    }

    [HttpPost("Registrar")]
    [AutorizarRoles(Roles.Admin, Roles.Diretor, Roles.DiretorAssociado, Roles.Secretario, Roles.Tesoureiro)]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<LancamentoGeralResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Registrar(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RegistrarLancamentoRequest request,
        CancellationToken cancellationToken)
    {
        var autenticado = User.TentarObterUsuarioId(out var usuarioSolicitanteId);
        if (request.AplicarATodosOsMembros && !autenticado)
        {
            return AcessoNegado();
        }

        request.IdempotencyKey = idempotencyKey;
        request.UsuarioSolicitanteId = usuarioSolicitanteId;

        var resultado = await mediator.Send(request, cancellationToken);

        return resultado.Outcome switch
        {
            RegistrarLancamentoOutcome.LancamentoUnicoCriado => CreatedAtAction(nameof(List), new { }, resultado.Lancamento),
            RegistrarLancamentoOutcome.GeralCriado => Ok(resultado.Geral),
            RegistrarLancamentoOutcome.GeralReutilizado => Ok(resultado.Geral),
            RegistrarLancamentoOutcome.GeralConflitoIdempotencia => Conflict(new ProblemDetails
            {
                Title = "Chave de idempotência já usada com dados diferentes.",
                Detail = "O header Idempotency-Key informado já foi usado em uma operação anterior com tipo, categoria, tipo de fluxo, valor ou vencimento diferentes.",
                Status = StatusCodes.Status409Conflict,
            }),
            _ => throw new InvalidOperationException($"Resultado inesperado: {resultado.Outcome}."),
        };
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LancamentoDto>> Update(Guid id, [FromBody] UpdateLancamentoRequest request, CancellationToken cancellationToken)
    {
        request.Id = id;
        var updated = await mediator.Send(request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await mediator.Send(new DeleteLancamentoCommand(id), cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    private UnauthorizedObjectResult AcessoNegado() => Unauthorized(new ProblemDetails
    {
        Title = "ACESSO NEGADO!",
        Status = StatusCodes.Status401Unauthorized,
    });
}
