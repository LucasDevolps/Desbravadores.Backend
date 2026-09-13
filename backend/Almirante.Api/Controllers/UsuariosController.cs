using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Usuarios")]
[Authorize]
public class UsuariosController(UsuariosService usuariosService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UsuarioListItemDto>>> List(CancellationToken cancellationToken)
    {
        var usuarios = await usuariosService.ListAsync(cancellationToken);
        return Ok(usuarios);
    }
}
