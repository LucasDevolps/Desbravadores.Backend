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

    [HttpGet("{id:guid}")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LancamentoDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var lancamento = await mediator.Send(new GetLancamentoQuery(id), cancellationToken);
        return lancamento is null ? NotFound() : Ok(lancamento);
    }

    [HttpPost("Registrar")]
    [EndpointSummary("Registra um lançamento para um membro ou, atomicamente, para todos.")]
    [EndpointDescription("Modo geral (aplicarATodosOsMembros: true): Idempotency-Key obrigatório; 200 com a operação (criada ou reenviada). " +
        "Modo individual: 201 com o lançamento. Idempotency-Key é opcional no modo individual: sem ele, cada POST cria uma nova cobrança " +
        "(nada é deduplicado por conteúdo); com ele, reenviar a mesma chave e os mesmos dados devolve 200 com o lançamento já criado, " +
        "e a mesma chave com dados diferentes (inclusive outro membro ou o outro modo) devolve 409. " +
        "Lançamentos de Entrada atribuídos a um membro (débitos) geram uma notificação para esse membro na mesma transação; Saída não notifica.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<LancamentoGeralResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Registrar(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RegistrarLancamentoRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioSolicitanteId))
        {
            return IdentidadeInvalida();
        }

        request.IdempotencyKey = idempotencyKey;
        request.UsuarioSolicitanteId = usuarioSolicitanteId;

        var resultado = await mediator.Send(request, cancellationToken);

        return resultado.Outcome switch
        {
            RegistrarLancamentoOutcome.LancamentoUnicoCriado => CreatedAtAction(nameof(GetById), new { id = resultado.Lancamento!.Id }, resultado.Lancamento),
            RegistrarLancamentoOutcome.LancamentoUnicoReutilizado => Ok(resultado.Lancamento),
            RegistrarLancamentoOutcome.GeralCriado => Ok(resultado.Geral),
            RegistrarLancamentoOutcome.GeralReutilizado => Ok(resultado.Geral),
            RegistrarLancamentoOutcome.GeralConflitoIdempotencia => Conflict(new ProblemDetails
            {
                Title = "Chave de idempotência já usada com dados diferentes.",
                Detail = "O header Idempotency-Key informado já foi usado em uma operação anterior com finalidade, descrição, categoria, tipo de fluxo, valor, vencimento, membro ou modo diferentes.",
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
        if (!User.TentarObterUsuarioId(out var usuarioResponsavelId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.UsuarioResponsavelId = usuarioResponsavelId;
        var updated = await mediator.Send(request, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpPost("{id:guid}/Pagamento")]
    [EndpointSummary("Registra o pagamento efetivo de um lançamento.")]
    [EndpointDescription("Corpo: dataPagamento (yyyy-MM-dd, até hoje no fuso do clube) e valorPago (> 0, 2 casas, no máximo o valor do lançamento). " +
        "Marca o lançamento como Pago e grava quem registrou. Aceita lançamentos Pendente, Atrasado ou \"Pago\" histórico sem dados de pagamento (regularização). " +
        "409 se o pagamento já foi registrado (use PUT para corrigir) ou se o lançamento mudou durante a requisição.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RegistrarPagamento(Guid id, [FromBody] RegistrarPagamentoRequest request, CancellationToken cancellationToken) =>
        EnviarPagamentoAsync(id, request, cancellationToken);

    [HttpPut("{id:guid}/Pagamento")]
    [EndpointSummary("Corrige data e/ou valor de um pagamento já registrado.")]
    [EndpointDescription("Mesmas regras do POST. 409 se o lançamento ainda não tem pagamento registrado ou mudou durante a requisição.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AtualizarPagamento(Guid id, [FromBody] AtualizarPagamentoRequest request, CancellationToken cancellationToken) =>
        EnviarPagamentoAsync(id, request, cancellationToken);

    private async Task<IActionResult> EnviarPagamentoAsync(Guid id, PagamentoLancamentoRequest request, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioResponsavelId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.UsuarioResponsavelId = usuarioResponsavelId;
        var lancamento = await mediator.Send(request, cancellationToken);
        return lancamento is null
            ? NotFound(new ProblemDetails { Title = "Lançamento não encontrado.", Status = StatusCodes.Status404NotFound })
            : Ok(lancamento);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
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

    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });
}
