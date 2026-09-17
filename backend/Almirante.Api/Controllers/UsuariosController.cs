using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Usuarios")]
[Authorize(Policy = Policies.GestaoCadastros)]
public class UsuariosController(UsuariosService usuariosService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UsuarioListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<UsuarioListItemDto>>> List(CancellationToken cancellationToken)
    {
        var usuarios = await usuariosService.ListAsync(cancellationToken);
        return Ok(usuarios);
    }
}
