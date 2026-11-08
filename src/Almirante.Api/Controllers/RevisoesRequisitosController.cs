using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Revisões dos requisitos de classes e especialidades (issue #125). Uma revisão nasce em rascunho com todos os requisitos, é publicada
/// (vira a vigente e substitui a anterior) ou descartada; requisitos nunca são editados. Quem já usa uma revisão continua nela.
/// </summary>
[ApiController]
[Route("api/RevisoesRequisitos")]
[Authorize(Policy = Policies.Autoatendimento)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class RevisoesRequisitosController(ISender mediator) : ControllerBase
{
    [HttpGet("{id:guid}", Name = nameof(ObterRevisao))]
    [EndpointSummary("Consulta uma revisão com a árvore de requisitos.")]
    [EndpointDescription("Rascunhos e descartadas só para GestaoFormacao (para os demais, 404). Cada requisito traz código, seção, ordem, " +
        "obrigatoriedade, regra do grupo (Todos ou Alternativas com quantidadeMinima), referência oficial e exigência de especialidades.")]
    [ProducesResponseType<RevisaoRequisitosDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RevisaoRequisitosDto>> ObterRevisao(Guid id, CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(new ObterRevisaoQuery(id, Roles.Formacao.Contains(User.Role())), cancellationToken);
        return dto is null ? NotFound(new ProblemDetails { Title = "Revisão não encontrada.", Status = StatusCodes.Status404NotFound }) : Ok(dto);
    }

    [HttpPost]
    [Authorize(Policy = Policies.GestaoFormacao)]
    [EndpointSummary("Cria uma revisão em rascunho com todos os requisitos (reprodução do cartão oficial, com fonte documentada).")]
    [EndpointDescription("classe OU especialidadeId; edicao e fonteOficial obrigatórias; observacoes registra orientações posteriores e divergências. " +
        "requisitos: árvore (até 4 níveis, 500 itens) na ordem do cartão. Itens com subitens exigem regra: Todos (todos os subitens obrigatórios) " +
        "ou Alternativas com quantidadeMinima (escolher uma = 1; N entre as opções = N). obrigatorio vale na raiz e em grupos Todos (padrão true); " +
        "opções de Alternativas não são obrigatórias. exigenciaEspecialidade (só em itens): quantidade, especialidadeId e/ou area, " +
        "naoRealizadaAnteriormente. Códigos únicos na revisão.")]
    [ProducesResponseType<RevisaoRequisitosDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Criar([FromBody] CriarRevisaoRequest request, CancellationToken cancellationToken)
    {
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
        var dto = await mediator.Send(request, cancellationToken);
        return CreatedAtRoute(nameof(ObterRevisao), new { id = dto.Id }, dto);
    }

    [HttpPost("{id:guid}/Publicar")]
    [Authorize(Policy = Policies.GestaoFormacao)]
    [EndpointSummary("Publica um rascunho: vira a revisão vigente e a anterior passa a substituída.")]
    [EndpointDescription("Corpo: versao. Novas associações usam a vigente; vínculos existentes continuam na revisão que usam até uma migração " +
        "explícita (POST /api/Formacao/.../{id}/MigrarRevisao). 409 se não for rascunho ou se outra publicação ocorrer ao mesmo tempo.")]
    [ProducesResponseType<RevisaoRequisitosDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RevisaoRequisitosDto>> Publicar(Guid id, [FromBody] AlterarSituacaoRevisaoRequest request, CancellationToken cancellationToken) =>
        AlterarSituacao(id, request, publicar: true, cancellationToken);

    [HttpPost("{id:guid}/Descartar")]
    [Authorize(Policy = Policies.GestaoFormacao)]
    [EndpointSummary("Descarta um rascunho (motivo obrigatório; o registro é preservado).")]
    [ProducesResponseType<RevisaoRequisitosDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RevisaoRequisitosDto>> Descartar(Guid id, [FromBody] AlterarSituacaoRevisaoRequest request, CancellationToken cancellationToken) =>
        AlterarSituacao(id, request, publicar: false, cancellationToken);

    private async Task<ActionResult<RevisaoRequisitosDto>> AlterarSituacao(Guid id, AlterarSituacaoRevisaoRequest request, bool publicar,
        CancellationToken cancellationToken)
    {
        request.Id = id;
        request.Publicar = publicar;
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
        return Ok(await mediator.Send(request, cancellationToken));
    }
}
