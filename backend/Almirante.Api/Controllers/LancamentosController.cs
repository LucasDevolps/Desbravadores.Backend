using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Lancamentos")]
[Authorize(Policy = Policies.GestaoFinanceira)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class LancamentosController(ISender mediator) : ControllerBase
{

    [HttpGet]
    [ProducesResponseType<LancamentosResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LancamentosResponse>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? finalidade = null,
        [FromQuery] DateOnly? vencimento = null,
        CancellationToken cancellationToken = default)
    {
        var response = await mediator.Send(
            new ListLancamentosQuery(page, pageSize, search, status, finalidade, vencimento), cancellationToken
        );
        return Ok(response);
    }

    [HttpPost("Registrar")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<LancamentoGeralResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Registrar(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RegistrarLancamentoRequest request,
        CancellationToken cancellationToken)
    {
        // O responsável (auditoria de CriadoPorUsuarioId) vem sempre da identidade autenticada.
        if (!User.TentarObterUsuarioId(out var usuarioSolicitanteId))
        {
            return IdentidadeInvalida();
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
            RegistrarLancamentoOutcome.MembroNaoEncontrado => NotFound(new ProblemDetails
            {
                Title = "Membro não encontrado.", Status = StatusCodes.Status404NotFound,
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
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteLancamentoRequest request, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId)) return IdentidadeInvalida();
        request.Id = id;
        request.UsuarioResponsavelId = usuarioId;
        request.IpResponsavel = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
        var deleted = await mediator.Send(request, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    // Inalcançável com o pipeline atual (OnTokenValidated exige sub GUID), mantido como defesa.
    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });
}
