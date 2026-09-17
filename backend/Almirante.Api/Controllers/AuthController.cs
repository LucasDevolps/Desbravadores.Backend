using System.Security.Claims;
using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController, Route("api/Auth")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AuthService authService, CookieCsrfProtection csrf) : ControllerBase
{
    private const string RefreshCookie = "__Host-almirante-refresh";

    /// <summary>Emite o token CSRF (e o cookie antiforgery) usado por login, refresh e logout.</summary>
    [HttpGet("csrf"), AllowAnonymous, OperacaoComCookie]
    [ProducesResponseType<CsrfTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<CsrfTokenResponse> Csrf() => Ok(new CsrfTokenResponse(csrf.IssueRequestToken(HttpContext)));

    /// <summary>Valida as credenciais, cria a sessão, define o cookie de refresh e devolve só o access token.</summary>
    [HttpPost("login"), AllowAnonymous, OperacaoComCookie(ValidarCsrf = true)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await authService.LoginAsync(request.Email, request.Senha, ct);
        if (result is null) return InvalidCredentials();
        SetRefreshCookie(result.RefreshToken, result.RefreshExpiresAtUtc);
        return Ok(result.Response);
    }

    /// <summary>Rotaciona o cookie de refresh e devolve um novo access token. Não exige bearer.</summary>
    [HttpPost("refresh"), AllowAnonymous, OperacaoComCookie(ValidarCsrf = true)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Refresh(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookie, out var raw)) return InvalidCredentials();
        var result = await authService.RefreshAsync(raw, ct);
        if (result is null) { DeleteRefreshCookie(); return InvalidCredentials(); }
        SetRefreshCookie(result.RefreshToken, result.RefreshExpiresAtUtc);
        return Ok(result.Response);
    }

    /// <summary>Revoga a sessão apresentada (cookie de refresh e/ou bearer) e remove o cookie. Idempotente.</summary>
    [HttpPost("logout"), AllowAnonymous, OperacaoComCookie(ValidarCsrf = true)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        Request.Cookies.TryGetValue(RefreshCookie, out var raw);
        Guid? sid = Guid.TryParse(User.FindFirstValue(JwtProfile.SessionClaim), out var parsedSid) ? parsedSid : null;
        Guid? uid = User.TentarObterUsuarioId(out var parsedUid) ? parsedUid : null;
        await authService.LogoutAsync(raw, sid, uid, ct);
        DeleteRefreshCookie();
        return NoContent();
    }

    /// <summary>Perfil atual do usuário autenticado (sempre do banco, nunca do JWT).</summary>
    [HttpGet("Me"), Authorize]
    [ProducesResponseType<MeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MeDto>> Me(CancellationToken ct)
    {
        if (!User.TentarObterUsuarioId(out var id)) return Unauthorized();
        var user = await authService.GetMeAsync(id, ct);
        return user is null ? Unauthorized() : Ok(user);
    }

    private ObjectResult InvalidCredentials() => Unauthorized(new ProblemDetails { Title = "Credenciais inválidas.", Status = 401 });
    private void SetRefreshCookie(string value, DateTime expiresUtc) => Response.Cookies.Append(RefreshCookie, value,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = new DateTimeOffset(expiresUtc, TimeSpan.Zero) });
    private void DeleteRefreshCookie() => Response.Cookies.Delete(RefreshCookie,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
}
