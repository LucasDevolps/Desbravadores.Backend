using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Cargos")]
[Authorize(Policy = Policies.GestaoCadastros)]
public class CargosController(CargosService cargosService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CargoDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<CargoDto>>> List(CancellationToken cancellationToken)
    {
        var cargos = await cargosService.ListAsync(cancellationToken);
        return Ok(cargos);
    }
}
