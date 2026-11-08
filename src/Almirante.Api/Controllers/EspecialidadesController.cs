using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Catálogo administrável de especialidades (issue #125). Consulta para qualquer cargo ativo; cadastro, edição, inativação e reativação
/// só para GestaoFormacao (ADM, DIR, DIRA, SEC). Nada é excluído fisicamente.
/// </summary>
[ApiController]
[Route("api/Especialidades")]
[Authorize(Policy = Policies.Autoatendimento)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class EspecialidadesController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista as especialidades, paginadas (por padrão só as ativas).")]
    [EndpointDescription("busca procura no nome e no código oficial; area filtra pela área exata (sem diferenciar maiúsculas); modalidade = Basica ou " +
        "Avancada; incluirInativas=true inclui as inativas. Ordem estável: área, nome, id. page a partir de 1; pageSize de 1 a 100 (padrão 20).")]
    [ProducesResponseType<PaginaDto<EspecialidadeDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginaDto<EspecialidadeDto>>> Listar(
        [FromQuery] string? busca = null,
        [FromQuery] string? area = null,
        [FromQuery] ModalidadeEspecialidade? modalidade = null,
        [FromQuery] bool incluirInativas = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = RegrasFormacao.PageSizePadrao,
        CancellationToken cancellationToken = default) =>
        Ok(await mediator.Send(new ListarEspecialidadesQuery(busca, area, modalidade, incluirInativas, page, pageSize), cancellationToken));

    [HttpGet("{id:guid}", Name = nameof(ObterEspecialidade))]
    [EndpointSummary("Consulta uma especialidade (ativa ou inativa).")]
    [ProducesResponseType<EspecialidadeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EspecialidadeDto>> ObterEspecialidade(Guid id, CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(new ObterEspecialidadeQuery(id), cancellationToken);
        return dto is null ? NaoEncontrada() : Ok(dto);
    }

    [HttpGet("{id:guid}/Revisoes")]
    [EndpointSummary("Lista as revisões dos requisitos da especialidade (mais recente primeiro).")]
    [EndpointDescription("Publicada e substituídas para qualquer cargo; rascunhos e descartadas só para GestaoFormacao.")]
    [ProducesResponseType<IReadOnlyList<RevisaoResumoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RevisaoResumoDto>>> ListarRevisoes(Guid id, CancellationToken cancellationToken)
    {
        var revisoes = await mediator.Send(new ListarRevisoesQuery(null, id, Roles.Formacao.Contains(User.Role())), cancellationToken);
        return revisoes is null ? NaoEncontrada() : Ok(revisoes);
    }

    [HttpPost]
    [Authorize(Policy = Policies.GestaoFormacao)]
    [EndpointSummary("Cadastra uma especialidade.")]
    [EndpointDescription("nome (até 150), area (até 100) e fonteOficial (até 300) obrigatórios. codigoOficial (até 20) é único no catálogo, inclusive " +
        "entre inativas (409); nome é único entre as ativas da mesma área e modalidade (409), sem bloquear nomes apenas semelhantes. " +
        "nivelDificuldade 1–3 e modalidade (Basica/Avancada) são independentes. preRequisitos: ids de especialidades ativas, sem ciclos. " +
        "referenciaDistintivo: URL https da imagem oficial. O cadastro não cria especialidade oficial: registre fonte e edição/revisão.")]
    [ProducesResponseType<EspecialidadeDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cadastrar([FromBody] CadastrarEspecialidadeRequest request, CancellationToken cancellationToken)
    {
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
        var dto = await mediator.Send(request, cancellationToken);
        return CreatedAtRoute(nameof(ObterEspecialidade), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.GestaoFormacao)]
    [EndpointSummary("Atualiza uma especialidade (estado anterior auditado em formacao_historico).")]
    [EndpointDescription("Mesmas regras do cadastro; substitui todos os campos (opcional omitido fica vazio) e a lista de pré-requisitos " +
        "(removidos são desativados, não apagados). Exige versao. Vínculos e progresso existentes não mudam.")]
    [ProducesResponseType<EspecialidadeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EspecialidadeDto>> Atualizar(Guid id, [FromBody] AtualizarEspecialidadeRequest request, CancellationToken cancellationToken)
    {
        request.Id = id;
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
        return Ok(await mediator.Send(request, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.GestaoFormacao)]
    [EndpointSummary("Inativa uma especialidade (exclusão lógica, auditada).")]
    [EndpointDescription("Corpo: motivo (1–255) e versao. Impede novas associações e revisões; consulta, progresso, conquistas e histórico " +
        "existentes continuam. 409 se já inativa ou versão desatualizada.")]
    [ProducesResponseType<EspecialidadeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<EspecialidadeDto>> Inativar(Guid id, [FromBody] AlterarSituacaoEspecialidadeRequest request, CancellationToken cancellationToken) =>
        AlterarSituacao(id, request, ativar: false, cancellationToken);

    [HttpPost("{id:guid}/Reativar")]
    [Authorize(Policy = Policies.GestaoFormacao)]
    [EndpointSummary("Reativa uma especialidade inativa.")]
    [EndpointDescription("Corpo: versao. 409 se já ativa ou se houver outra ativa com o mesmo nome, área e modalidade.")]
    [ProducesResponseType<EspecialidadeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<EspecialidadeDto>> Reativar(Guid id, [FromBody] AlterarSituacaoEspecialidadeRequest request, CancellationToken cancellationToken) =>
        AlterarSituacao(id, request, ativar: true, cancellationToken);

    private async Task<ActionResult<EspecialidadeDto>> AlterarSituacao(Guid id, AlterarSituacaoEspecialidadeRequest request, bool ativar,
        CancellationToken cancellationToken)
    {
        request.Id = id;
        request.Ativar = ativar;
        request.UsuarioId = User.UsuarioId();
        request.IpResponsavel = HttpContext.IpDeOrigem();
        return Ok(await mediator.Send(request, cancellationToken));
    }

    private NotFoundObjectResult NaoEncontrada() =>
        NotFound(new ProblemDetails { Title = "Especialidade não encontrada.", Status = StatusCodes.Status404NotFound });
}
