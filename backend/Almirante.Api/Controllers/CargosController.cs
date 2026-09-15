using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Cargos")]
[Authorize]
public class CargosController(CargosService cargosService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CargoDto>>> List(CancellationToken cancellationToken)
    {
        var cargos = await cargosService.ListAsync(cancellationToken);
        return Ok(cargos);
    }
}
