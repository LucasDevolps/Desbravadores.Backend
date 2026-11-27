using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Lancamentos")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class LancamentosController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [RequerPermissao(Permissoes.LancamentosConsultar)]
    [EndpointSummary("Lista os lançamentos, paginados, com filtros por busca, status, finalidade e vencimento.")]
    [EndpointDescription("Sem filtros ativos (search, status, finalidade e vencimento ausentes; status/finalidade \"Todos\" contam como ausentes), " +
        "a ordem é por prioridade de status: Atrasado, depois Pendente, depois Pago; dentro do mesmo status, vencimento mais recente primeiro e Id como desempate. " +
        "Com qualquer filtro ativo, a ordem é vencimento mais recente primeiro e Id como desempate. " +
        "Status atualizado automaticamente: um lançamento ativo Pendente com vencimento anterior a hoje (fuso do clube) passa a " +
        "Atrasado na subida da API e depois a cada hora ou logo após a virada do dia. Vencimento igual a hoje continua Pendente.")]
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
    [RequerPermissao(Permissoes.LancamentosConsultar)]
    [EndpointSummary("Consulta um lançamento ativo.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LancamentoDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var lancamento = await mediator.Send(new GetLancamentoQuery(id), cancellationToken);
        return lancamento is null ? NotFound() : Ok(lancamento);
    }

    [HttpPost("Registrar")]
    [RequerPermissao(Permissoes.LancamentosCriar)]
    [EndpointSummary("Registra um lançamento para um membro ou, atomicamente, para todos.")]
    [EndpointDescription("Modo geral (aplicarATodosOsMembros: true): Idempotency-Key obrigatório; 200 com a operação (criada ou reenviada). " +
        "Modo individual: 201 com o lançamento. Idempotency-Key é opcional no modo individual: sem ele, cada POST cria uma nova cobrança " +
        "(nada é deduplicado por conteúdo); com ele, reenviar a mesma chave e os mesmos dados devolve 200 com o lançamento já criado, " +
        "e a mesma chave com dados diferentes (inclusive outro membro ou o outro modo) devolve 409. " +
        "Lançamentos de Entrada atribuídos a um membro (débitos) geram uma notificação para esse membro na mesma transação; Saída não notifica. " +
        "Só membros ativos são cobrados: no modo individual, membro inexistente ou inativo devolve 404; no modo geral, inativos ficam de fora.")]
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
    [RequerPermissao(Permissoes.LancamentosEditar)]
    [EndpointSummary("Atualiza um lançamento.")]
    [EndpointDescription("Campos omitidos não mudam. status Pago só pelo endpoint de pagamento (409). Trocar o status para Pendente com vencimento " +
        "anterior a hoje no fuso do clube retorna 400: o lançamento vencido e não pago fica Atrasado (a rotina de vencimento o marcaria de novo). " +
        "Cobrança de evento: aceita status e valor (ajuste individual: > 0, 2 casas). O ajuste vale só para essa cobrança, não muda o valor padrão " +
        "do evento nem as demais e é preservado nas edições do evento (valorAjustado = true enquanto difere do valor por membro). " +
        "409 com codigo COBRANCA_EVENTO_COM_PAGAMENTO se a cobrança já tem pagamento, inclusive parcial; 409 para membro, vencimento, finalidade, " +
        "categoria ou tipo de fluxo, para devoluções e quando o lançamento mudou durante a requisição.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
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
    [RequerPermissao(Permissoes.PagamentosRegistrar)]
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
    [RequerPermissao(Permissoes.PagamentosRegistrar)]
    [EndpointSummary("Corrige data e/ou valor de um pagamento já registrado.")]
    [EndpointDescription("Mesmas regras do POST. 409 se o lançamento ainda não tem pagamento registrado ou mudou durante a requisição.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AtualizarPagamento(Guid id, [FromBody] AtualizarPagamentoRequest request, CancellationToken cancellationToken) =>
        EnviarPagamentoAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/Devolucao")]
    [RequerPermissao(Permissoes.PagamentosRegistrar)]
    [EndpointSummary("Dá baixa em uma devolução de evento (o tesoureiro devolveu o dinheiro ao membro).")]
    [EndpointDescription("Só para devoluções (lançamentos com lancamentoOrigemId). Corpo: dataPagamento (yyyy-MM-dd, até hoje no fuso do clube), " +
        "valorPago (valor efetivamente devolvido: > 0, 2 casas, no máximo o valor esperado) e justificativa. A justificativa é obrigatória quando " +
        "valorPago difere do valor esperado e opcional quando é igual; quando informada, precisa de pelo menos 3 caracteres sem contar os espaços " +
        "(máx. 255). Marca a devolução como Paga. 409 se o lançamento não é devolução, se a baixa já foi registrada (use PUT) ou se mudou durante a requisição.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> RegistrarDevolucao(Guid id, [FromBody] RegistrarDevolucaoRequest request, CancellationToken cancellationToken) =>
        EnviarDevolucaoAsync(id, request, cancellationToken);

    [HttpPut("{id:guid}/Devolucao")]
    [RequerPermissao(Permissoes.PagamentosRegistrar)]
    [EndpointSummary("Corrige data, valor e/ou justificativa de uma devolução já baixada.")]
    [EndpointDescription("Mesmas regras do POST (a justificativa enviada substitui a anterior). 409 se a devolução ainda não tem baixa registrada.")]
    [ProducesResponseType<LancamentoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AtualizarDevolucao(Guid id, [FromBody] AtualizarDevolucaoRequest request, CancellationToken cancellationToken) =>
        EnviarDevolucaoAsync(id, request, cancellationToken);

    private async Task<IActionResult> EnviarDevolucaoAsync(Guid id, DevolucaoLancamentoRequest request, CancellationToken cancellationToken)
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
    [RequerPermissao(Permissoes.LancamentosExcluir)]
    [EndpointSummary("Exclui logicamente um lançamento; a exclusão é auditada.")]
    [EndpointDescription("Corpo: motivo (1–255). Cobrança de evento sem nenhum pagamento é excluída como isenção individual: a cobrança é " +
        "desativada com auditoria (lancamentos_deletados), o membro continua inscrito e aparece em isencoes do evento, e as edições do evento " +
        "não recriam a cobrança. 409 com codigo COBRANCA_EVENTO_COM_PAGAMENTO se há pagamento, inclusive parcial (nenhum pagamento é apagado); " +
        "409 para devoluções. 404 se inexistente, já excluído ou de evento excluído.")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
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
