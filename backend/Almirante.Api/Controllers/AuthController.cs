using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController, Route("api/Auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AuthService authService, IAntiforgery antiforgery) : ControllerBase
{
    private const string RefreshCookie = "__Host-almirante-refresh";

    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf() => Ok(new { csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpPost("login"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await authService.LoginAsync(request.Email, request.Senha, ct);
        if (result is null) return InvalidCredentials();
        SetRefreshCookie(result.RefreshToken, result.RefreshExpiresAtUtc);
        return Ok(result.Response);
    }

    [HttpPost("refresh"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<ActionResult<LoginResponse>> Refresh(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookie, out var raw)) return InvalidCredentials();
        AuthResult? result;
        try { result = await authService.RefreshAsync(raw, ct); } catch (FormatException) { result = null; }
        if (result is null) { DeleteRefreshCookie(); return InvalidCredentials(); }
        SetRefreshCookie(result.RefreshToken, result.RefreshExpiresAtUtc);
        return Ok(result.Response);
    }

    [HttpPost("logout"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        Request.Cookies.TryGetValue(RefreshCookie, out var raw);
        Guid? sid = Guid.TryParse(User.FindFirstValue("sid"), out var parsedSid) ? parsedSid : null;
        Guid? uid = User.TentarObterUsuarioId(out var parsedUid) ? parsedUid : null;
        await authService.LogoutAsync(raw, sid, uid, ct);
        DeleteRefreshCookie();
        return NoContent();
    }

    [HttpGet("Me"), Authorize]
    public async Task<ActionResult<MeDto>> Me(CancellationToken ct)
    {
        if (!User.TentarObterUsuarioId(out var id)) return Unauthorized();
        var user = await authService.GetMeAsync(id, ct);
        return user is null ? Unauthorized() : Ok(user);
    }

    private ObjectResult InvalidCredentials() => Unauthorized(new ProblemDetails { Title = "Credenciais inválidas.", Status = 401 });
    private void SetRefreshCookie(string value, DateTime expires) => Response.Cookies.Append(RefreshCookie, value,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = expires });
    private void DeleteRefreshCookie() => Response.Cookies.Delete(RefreshCookie,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
}
