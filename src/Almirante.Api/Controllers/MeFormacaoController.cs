using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Consulta da própria formação (classes, especialidades, requisitos e conquistas) por qualquer cargo ativo. O membro vem só do token:
/// não há id de membro na rota, e o id de um vínculo de outro membro responde 404 como se não existisse. Somente leitura.
/// </summary>
[ApiController]
[Route("api/Me/Formacao")]
[Authorize(Policy = Policies.Autoatendimento)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class MeFormacaoController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista as próprias classes e especialidades (inclusive concluídas, históricas e canceladas), com o resumo do progresso.")]
    [ProducesResponseType<FormacaoMembroDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<FormacaoMembroDto>> Obter(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ObterFormacaoMembroQuery(User.UsuarioId()), cancellationToken));

    [HttpGet("Classes/{id:guid}")]
    [EndpointSummary("Consulta a evolução e as pendências de uma classe própria.")]
    [EndpointDescription("Mesma representação de GET /api/Formacao/Classes/{id}. 404 para vínculo inexistente ou de outro membro.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ActionResult<VinculoDto>> ObterClasse(Guid id, CancellationToken cancellationToken) =>
        ObterVinculo(TipoVinculo.Classe, id, cancellationToken);

    [HttpGet("Especialidades/{id:guid}")]
    [EndpointSummary("Consulta a evolução e as pendências de uma especialidade própria.")]
    [EndpointDescription("Mesma representação de GET /api/Formacao/Especialidades/{id}. 404 para vínculo inexistente ou de outro membro.")]
    [ProducesResponseType<VinculoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ActionResult<VinculoDto>> ObterEspecialidade(Guid id, CancellationToken cancellationToken) =>
        ObterVinculo(TipoVinculo.Especialidade, id, cancellationToken);

    private async Task<ActionResult<VinculoDto>> ObterVinculo(TipoVinculo tipo, Guid id, CancellationToken cancellationToken)
    {
        var dto = await mediator.Send(new ObterVinculoQuery(tipo, id, User.UsuarioId()), cancellationToken);
        return dto is null ? NotFound(new ProblemDetails { Title = "Vínculo não encontrado.", Status = StatusCodes.Status404NotFound }) : Ok(dto);
    }
}
