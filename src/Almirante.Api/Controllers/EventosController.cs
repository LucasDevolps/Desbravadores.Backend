using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Gestão de eventos (eventos.consultar/criar/editar/excluir). Cadastro e cobranças são uma operação só: criar, editar ou excluir um
/// evento gera, sincroniza ou desativa os lançamentos pendentes dos participantes na mesma transação (idempotência, rowversion e
/// auditoria preservadas). Registrar ou corrigir pagamentos continua em /api/Lancamentos (financeiro.pagamentos.registrar). Participantes consultam
/// os próprios eventos em /api/Me/Eventos.
/// </summary>
[ApiController]
[Route("api/Eventos")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class EventosController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [RequerPermissao(Permissoes.EventosConsultar)]
    [EndpointSummary("Lista os cadastros de eventos ativos no período.")]
    [EndpointDescription("Sem parâmetros: do primeiro dia do mês atual menos 30 dias até o último dia do mês atual mais 30 dias (UTC). " +
        "Com filtro, dataInicial e dataFinal (yyyy-MM-dd, inclusivas) são obrigatórias juntas e podem cobrir períodos históricos, com no máximo 366 dias. " +
        "Lista vazia devolve items: [] (nunca 404). dataMinimaCadastro é o primeiro dia do mês corrente (UTC).")]
    [ProducesResponseType<EventosResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EventosResponse>> List(
        [FromQuery] DateOnly? dataInicial = null,
        [FromQuery] DateOnly? dataFinal = null,
        CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new ListEventosQuery(dataInicial, dataFinal), cancellationToken));

    [HttpGet("{id:guid}", Name = nameof(GetById))]
    [RequerPermissao(Permissoes.EventosConsultar)]
    [EndpointSummary("Consulta um cadastro de evento ativo.")]
    [ProducesResponseType<EventoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EventoDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var evento = await mediator.Send(new GetEventoQuery(id), cancellationToken);
        return evento is null
            ? NotFound(new ProblemDetails { Title = "Evento não encontrado.", Status = StatusCodes.Status404NotFound })
            : Ok(evento);
    }

    [HttpPost]
    [RequerPermissao(Permissoes.EventosCriar)]
    [EndpointSummary("Cadastra um grupo de cobrança de evento e, quando há valor a pagar, gera um lançamento pendente por membro.")]
    [EndpointDescription("Exige o header Idempotency-Key (UUID). O campo membros aceita um GUID OU um array de GUIDs (mesma operação). " +
        "A data mínima é o primeiro dia do mês atual (referência UTC do servidor, não a data de hoje). " +
        "titulo é obrigatório (1–150) e descricao é opcional (até 2000, aceita quebras de linha). " +
        "Cada membro recebe um lançamento com valorPorMembro = transporte efetivo + alimentação efetiva + seguro; total = valorPorMembro × membros. " +
        "Com valorPorMembro zero (ex.: reunião de clube), os membros participam e são notificados, mas nenhum lançamento é gerado (lancamentos: []). " +
        "201: criado. 200: reenvio idempotente (mesma chave e mesma operação normalizada) — devolve o RESULTADO ORIGINAL do POST, com a versao original, " +
        "não o estado atual (consulte o GET antes de editar; um PUT com a versao original recebe 409 se o cadastro mudou). O reenvio vale mesmo depois da virada do mês; " +
        "a data mínima só se aplica a operações novas. 409: chave com dados diferentes, evento já excluído, membro já em outro grupo ativo do mesmo passeio, " +
        "passeio em uso por outra operação, ou operação registrada antes de a resposta original ser guardada (codigo EVENTO_IDEMPOTENCIA_SEM_RESPOSTA_ORIGINAL: consulte o evento existente). " +
        "404: eventoReferenciaId inexistente/inativo.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<EventoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<EventoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Registrar(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RegistrarEventoRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        request.IdempotencyKey = idempotencyKey;
        request.UsuarioSolicitanteId = usuarioId;
        var resultado = await mediator.Send(request, cancellationToken);
        return resultado.Criado
            ? CreatedAtRoute(nameof(GetById), new { id = resultado.Evento.Id }, resultado.Evento)
            : Ok(resultado.Evento);
    }

    [HttpPut("{id:guid}")]
    [RequerPermissao(Permissoes.EventosEditar)]
    [EndpointSummary("Atualiza um cadastro de evento e sincroniza participantes e lançamentos.")]
    [EndpointDescription("Exige versao (rowversion). Sem lançamento Pago, atualiza mantidos, cria novos e desativa removidos " +
        "(motivo obrigatório na remoção); um lançamento Atrasado acompanha o novo valor e a nova data e volta a Pendente se a data ainda não passou. " +
        "Valor por membro que passa de zero a positivo cria o lançamento pendente de cada participante; de positivo a zero desativa as cobranças " +
        "em aberto (motivo obrigatório) e mantém os participantes. " +
        "Com lançamento Pago: o valor por membro pode DIMINUIR (inclusive a zero), com motivo; as cobranças em aberto acompanham o novo valor " +
        "(ou são canceladas no zero), os lançamentos Pagos não mudam, e cada pagamento acima do novo valor gera uma devolução pendente " +
        "(Saída para o membro, valor = valorPago − novo valor − devoluções já geradas, listada em devolucoes) e notifica o membro. " +
        "Aumento, ou outra composição de custos com o mesmo total, inclusão de participantes e mudança de data retornam 409. " +
        "Quem já pagou só é removido antes da data do evento depois do estorno concluído (devolução gerada e baixada); no dia do evento ou depois, " +
        "sai sem devolução e o pagamento continua no histórico. Quem não pagou sai normalmente (motivo obrigatório). " +
        "Pago histórico sem dados de pagamento não gera devolução: registre o pagamento antes (409). " +
        "Local, título e descrição ainda podem ser corrigidos (a descrição dos lançamentos e devoluções acompanha o título). " +
        "membros aceita GUID ou GUID[] como no POST. Data e local não mudam quando o passeio tem mais de um cadastro ativo (409).")]
    [ProducesResponseType<EventoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEventoRequest request, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.UsuarioResponsavelId = usuarioId;
        request.IpResponsavel = IpDeOrigem();
        return Ok(await mediator.Send(request, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    [RequerPermissao(Permissoes.EventosExcluir)]
    [EndpointSummary("Exclui logicamente um cadastro de evento (auditado por trigger).")]
    [EndpointDescription("Corpo: motivo (1–255) e versao. Desativa o cadastro, seus participantes e lançamentos em aberto (Pendente ou Atrasado) na mesma " +
        "transação; pagamentos e devoluções baixadas continuam ativos (histórico do caixa). Outros grupos do passeio permanecem. " +
        "409 enquanto houver valor retido (pagamento não devolvido por inteiro, ou devolução ainda não baixada) ou versão desatualizada; " +
        "404 se inexistente ou já excluído.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteEventoRequest request, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.UsuarioResponsavelId = usuarioId;
        request.IpResponsavel = IpDeOrigem();
        await mediator.Send(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/Participantes/{membroId:guid}/Estorno")]
    [RequerPermissao(Permissoes.EventosEditar)]
    [EndpointSummary("Gera a devolução de tudo o que o membro ainda tem retido nos pagamentos do evento (estorno do participante).")]
    [EndpointDescription("Corpo: motivo (1–255) e versao. Para cada pagamento do membro no cadastro, cria uma devolução pendente (Saída) de " +
        "valorPago − devoluções já geradas e notifica o membro; o pagamento original não muda e o participante continua no evento. " +
        "Depois de dar baixa na devolução (POST /api/Lancamentos/{id}/Devolucao), o membro pode ser removido mesmo antes da data do evento. " +
        "Vale também para quem já foi removido e ainda tem pagamento ativo. 200 com o evento atualizado (devolucoes). " +
        "404: evento inexistente/excluído, ou membro sem participação nem lançamento no cadastro. " +
        "409: nada a devolver (sem pagamento, ou tudo já devolvido/em devolução), Pago histórico sem dados de pagamento, versão desatualizada " +
        "ou passeio em uso.")]
    [ProducesResponseType<EventoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EstornarParticipante(Guid id, Guid membroId, [FromBody] EstornarParticipanteRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.MembroId = membroId;
        request.UsuarioResponsavelId = usuarioId;
        return Ok(await mediator.Send(request, cancellationToken));
    }

    private string IpDeOrigem() => HttpContext.IpDeOrigem();

    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });
}
