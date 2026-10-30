using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>Consulta de cargos para o cadastro de usuários (GestaoCadastros: ADM, DIR, DIRA, SEC). Não há criação nem edição de cargos pela API.</summary>
[ApiController]
[Route("api/Cargos")]
[Authorize(Policy = Policies.GestaoCadastros)]
public class CargosController(CargosService cargosService) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista os cargos para o cadastro de usuários.")]
    [ProducesResponseType<IReadOnlyList<CargoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<CargoDto>>> List(CancellationToken cancellationToken)
    {
        var cargos = await cargosService.ListAsync(cancellationToken);
        return Ok(cargos);
    }
}
