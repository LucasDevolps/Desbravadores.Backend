using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Auth")]
public class AuthController(AuthService authService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var response = await authService.LoginAsync(request.Email, request.Senha, cancellationToken);
        if (response is null)
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Credenciais inválidas.",
                Status = StatusCodes.Status401Unauthorized,
            });
        }

        return Ok(response);
    }

    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout()
    {
        // MVP: JWT é stateless. Revogação de token e sessão persistida ficaram fora do escopo.
        return NoContent();
    }

    [HttpGet("Me")]
    [Authorize]
    public async Task<ActionResult<UsuarioDto>> Me(CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var id))
        {
            return Unauthorized();
        }

        var usuario = await authService.GetByIdAsync(id, cancellationToken);
        if (usuario is null)
        {
            return Unauthorized();
        }

        return Ok(usuario);
    }
}
