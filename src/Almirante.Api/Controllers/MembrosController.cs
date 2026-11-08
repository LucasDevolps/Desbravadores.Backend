using Almirante.Api.Dtos;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Membros")]
[Authorize(Policy = Policies.ConsultaMembros)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class MembrosController(ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista os membros ativos com o mínimo para identificá-los (seletor de participantes e cobrados).")]
    [EndpointDescription("id, nome, cargo (nome) e role, em ordem de nome. Sem CPF, RG, contato, saúde, responsável, endereço ou foto: " +
        "o cadastro completo continua em GET /api/Usuarios (GestaoCadastros). Policy ConsultaMembros: ADM, DIR, DIRA, SEC, TES.")]
    [ProducesResponseType<IReadOnlyList<MembroResumoDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MembroResumoDto>>> Listar(CancellationToken cancellationToken) =>
        Ok(await mediator.Send(new ListarMembrosQuery(), cancellationToken));
}
