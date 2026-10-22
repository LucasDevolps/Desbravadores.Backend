using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>Visão administrativa e relatórios da formação (GestaoFormacao: ADM, DIR, DIRA, SEC).</summary>
[ApiController]
[Route("api/Formacao")]
[Authorize(Policy = Policies.GestaoFormacao)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class FormacaoController(ISender mediator) : ControllerBase
{
    [HttpGet("Membros/{membroId:guid}")]
    [EndpointSummary("Histórico individual de classes e especialidades de um membro (todas as situações, inclusive canceladas e históricas).")]
    [EndpointDescription("Classes na ordem do catálogo e especialidades por nome, cada uma com situação, datas, investidura/entrega e resumo do " +
        "progresso (requisitos cumpridos e pendentes). O detalhe de cada vínculo está em /api/Formacao/Classes/{id} e /api/Formacao/Especialidades/{id}.")]
    [ProducesResponseType<FormacaoMembroDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FormacaoMembroDto>> ObterMembro(Guid membroId, CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(new ObterFormacaoMembroQuery(membroId), cancellationToken);
        return dto is null ? NotFound(new ProblemDetails { Title = "Membro não encontrado.", Status = StatusCodes.Status404NotFound }) : Ok(dto);
    }

    [HttpGet("Relatorios/Participantes")]
    [EndpointSummary("Participantes por classe/especialidade e situação (em andamento, concluídas, aguardando investidura/entrega, canceladas).")]
    [EndpointDescription("tipo = Classe (padrão) ou Especialidade. Classe: todas as classes do filtro (classe, grupo), inclusive com zero. " +
        "Especialidade: as que têm vínculos, filtráveis por especialidadeId e area. A lista nominal de cada grupo sai de GET " +
        "/api/Formacao/Classes ou /api/Formacao/Especialidades com os mesmos filtros (concluídos aguardando: aguardandoInvestiduraOuEntrega=true).")]
    [ProducesResponseType<IReadOnlyList<ParticipantesPorSituacaoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ParticipantesPorSituacaoDto>>> RelatorioParticipantes(
        [FromQuery] TipoVinculo tipo = TipoVinculo.Classe,
        [FromQuery] ClasseDesbravador? classe = null,
        [FromQuery] GrupoClasse? grupo = null,
        [FromQuery] Guid? especialidadeId = null,
        [FromQuery] string? area = null,
        CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new RelatorioParticipantesQuery(tipo, classe, grupo, especialidadeId, area), cancellationToken));
}
