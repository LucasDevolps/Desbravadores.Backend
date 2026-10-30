using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Catálogo fixo das 15 classes (issue #125): consulta para qualquer cargo ativo, sem CRUD. A rota aceita o código (ex.: Amigo,
/// LiderMaster) ou o id numérico estável do enum.
/// </summary>
[ApiController]
[Route("api/Classes")]
[Authorize(Policy = Policies.Autoatendimento)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class ClassesController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista as 15 classes (regulares, avançadas e de liderança) com grupo, ordem, idades, pré-requisitos e revisão vigente.")]
    [EndpointDescription("Catálogo fixo identificado pelo enum ClasseDesbravador (id estável: 1–6 regulares, 11–16 avançadas, 21–23 liderança). " +
        "idadeReferencia é a faixa indicada, não limite máximo, e nada é atribuído pela idade. idadeMinimaInicio/idadeMinimaInvestidura, " +
        "preRequisitosConclusao e requisitosExternos existem só onde a referência oficial (referencias) os estabelece. Classes agrupadas são " +
        "uma modalidade (permiteModalidadeAgrupada), não uma classe. revisaoVigente é a revisão publicada dos requisitos (null sem revisão).")]
    [ProducesResponseType<ClassesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ClassesResponse>> Listar(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ListarClassesQuery(), cancellationToken));

    [HttpGet("{classe}")]
    [EndpointSummary("Consulta uma classe do catálogo.")]
    [ProducesResponseType<ClasseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClasseDto>> Obter(ClasseDesbravador classe, CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(new ObterClasseQuery(classe), cancellationToken);
        return dto is null ? NotFound(new ProblemDetails { Title = "Classe não encontrada.", Status = StatusCodes.Status404NotFound }) : Ok(dto);
    }

    [HttpGet("{classe}/Revisoes")]
    [EndpointSummary("Lista as revisões dos requisitos da classe (mais recente primeiro).")]
    [EndpointDescription("Publicada e substituídas para qualquer cargo; rascunhos e descartadas só para GestaoFormacao (ADM, DIR, DIRA, SEC). " +
        "Os requisitos de cada revisão estão em GET /api/RevisoesRequisitos/{id}.")]
    [ProducesResponseType<IReadOnlyList<RevisaoResumoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RevisaoResumoDto>>> ListarRevisoes(ClasseDesbravador classe, CancellationToken cancellationToken)
    {
        var revisoes = await mediator.Send(new ListarRevisoesQuery(classe, null, Roles.Formacao.Contains(User.Role())), cancellationToken);
        return revisoes is null ? NotFound(new ProblemDetails { Title = "Classe não encontrada.", Status = StatusCodes.Status404NotFound }) : Ok(revisoes);
    }
}
