using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

// Controller fino: autenticação/autorização (mesma política financeira dos lançamentos), coleta de dados
// que só o servidor pode fornecer (responsável das claims, IP após o middleware de proxies confiáveis,
// Idempotency-Key) e envio ao MediatR. Nenhuma regra de negócio aqui.
[ApiController]
[Route("api/Eventos")]
[Authorize(Policy = Policies.GestaoFinanceira)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class EventosController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista os cadastros de eventos ativos no período.")]
    [EndpointDescription("Sem parâmetros: do primeiro dia do mês atual menos 30 dias até o último dia do mês atual mais 30 dias (UTC). " +
        "Com filtro, dataInicial e dataFinal (yyyy-MM-dd, inclusivas) são obrigatórias juntas e podem cobrir períodos históricos. " +
        "Lista vazia devolve items: [] (nunca 404). dataMinimaCadastro é o primeiro dia do mês corrente (UTC).")]
    [ProducesResponseType<EventosResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EventosResponse>> List(
        [FromQuery] DateOnly? dataInicial = null,
        [FromQuery] DateOnly? dataFinal = null,
        CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new ListEventosQuery(dataInicial, dataFinal), cancellationToken));

    [HttpGet("{id:guid}", Name = nameof(GetById))]
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
    [EndpointSummary("Cadastra um grupo de cobrança de evento e gera um lançamento pendente por membro.")]
    [EndpointDescription("Exige o header Idempotency-Key (UUID). O campo membros aceita um GUID OU um array de GUIDs (mesma operação). " +
        "A data mínima é o primeiro dia do mês atual (referência UTC do servidor, não a data de hoje). " +
        "Cada membro recebe um lançamento com valorPorMembro = transporte efetivo + alimentação efetiva + seguro; total = valorPorMembro × membros. " +
        "201: criado. 200: reenvio idempotente (mesma chave e mesma operação normalizada). 409: chave com dados diferentes, evento já excluído, " +
        "ou membro já em outro grupo ativo do mesmo passeio. 404: eventoReferenciaId inexistente/inativo.")]
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
    [EndpointSummary("Atualiza um cadastro de evento e sincroniza participantes e lançamentos.")]
    [EndpointDescription("Exige versao (rowversion). Com todos os lançamentos pendentes, atualiza mantidos, cria novos e desativa removidos " +
        "(motivo obrigatório na remoção). Com lançamento Pago/Atrasado, alterações de valor, participantes ou data retornam 409; o local ainda pode ser corrigido. " +
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
    [EndpointSummary("Exclui logicamente um cadastro de evento (auditado por trigger).")]
    [EndpointDescription("Corpo: motivo (1–255) e versao. Desativa o cadastro, seus participantes e lançamentos pendentes na mesma transação; " +
        "outros grupos do passeio permanecem. 409 se houver lançamento Pago/Atrasado ou versão desatualizada; 404 se inexistente ou já excluído.")]
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

    // IP observado pela conexão depois do middleware de proxies confiáveis (ForwardedHeaders); nunca lido de
    // X-Forwarded-For manualmente nem do corpo. IPv4 mapeado em IPv6 vira IPv4 puro.
    private string IpDeOrigem()
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "0.0.0.0";
        }

        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    // Inalcançável com o pipeline atual (OnTokenValidated exige sub GUID), mantido como defesa.
    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });
}
