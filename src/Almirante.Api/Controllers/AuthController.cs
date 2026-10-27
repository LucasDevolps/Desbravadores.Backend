using System.Security.Claims;
using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Almirante.Api.Controllers;

[ApiController, Route("api/Auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AuthService authService, IAntiforgery antiforgery, ISender mediator, LimitadorTentativasSenha limitador)
    : ControllerBase
{
    private const string RefreshCookie = "__Host-almirante-refresh";

    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf() => Ok(new { csrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpPost("login"), AllowAnonymous, ValidateAntiForgeryToken]
    [EnableRateLimiting(LoginRateLimiting.PolicyName), SemAuditoriaHttp]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await authService.LoginAsync(request.Email, request.Senha, ct);
        if (result is null) return InvalidCredentials();
        SetRefreshCookie(result.RefreshToken, result.RefreshExpiresAtUtc);
        return Ok(result.Response);
    }

    [HttpPost("refresh"), AllowAnonymous, ValidateAntiForgeryToken]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
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
    [ProducesResponseType<MeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MeDto>> Me(CancellationToken ct)
    {
        if (!User.TentarObterUsuarioId(out var id)) return Unauthorized();
        var user = await authService.GetMeAsync(id, ct);
        return user is null ? Unauthorized() : Ok(user);
    }

    [HttpPost("alterar-senha"), Authorize(Policy = Policies.Autoatendimento), ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Altera a própria senha, confirmando a senha atual.")]
    [EndpointDescription("Qualquer usuário autenticado, com conta, cargo e sessão ativos; o alvo é sempre o usuário do token (não há id no corpo). " +
        "Corpo: senhaAtualSolicitante (a senha atual do próprio usuário), novaSenha e confirmacaoNovaSenha (igualdade exata, sem trim nem " +
        "diferença de caixa). A nova senha segue a política compartilhada: 15 a 128 caracteres Unicode, espaços e frases aceitos, sem senhas " +
        "comuns, repetidas, sequenciais, com o e-mail ou iguais à atual. Exige X-CSRF-TOKEN. Sucesso: 204 sem corpo; todas as sessões do " +
        "usuário são revogadas (access e refresh tokens anteriores deixam de valer), o cookie de refresh é removido e um novo login é " +
        "obrigatório; nenhum token novo é emitido e nenhum e-mail é enviado. 400 dados inválidos ou senha atual incorreta (nada é alterado), " +
        "401 sessão inválida, 409 conta alterada ao mesmo tempo, 429 excesso de tentativas (por usuário e IP).")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> AlterarSenha([FromBody] AlterarPropriaSenhaRequest request, CancellationToken ct)
    {
        var usuarioId = User.UsuarioId();
        var ip = HttpContext.IpDeOrigem();
        using (var tentativa = limitador.RegistrarTentativa(usuarioId, ip))
        {
            if (!tentativa.IsAcquired)
            {
                return RespostaMuitasTentativas.Criar(Response, tentativa, "Muitas tentativas de alteração de senha.",
                    "Aguarde antes de tentar alterar a senha novamente.");
            }
        }

        request.Contexto = new ContextoSolicitanteSenha(usuarioId, User.SessaoId(), ip, HttpContext.TraceIdSeguro());
        await mediator.Send(request, ct);
        DeleteRefreshCookie();
        return NoContent();
    }

    [HttpPost("recuperacao-senha/solicitar"), AllowAnonymous, ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Pede ajuda para recuperar o acesso, sem login.")]
    [EndpointDescription("Corpo: somente email. Resposta sempre 202 com a mesma mensagem, exista ou não a conta, esteja ativa ou não e mesmo " +
        "em pedidos repetidos: não revela cadastro, não envia e-mail, não devolve token nem dados, não altera senha, sessões ou bloqueio de " +
        "login. O pedido de uma conta que a matriz permite atender (Secretário, Tesoureiro, Desbravador) fica numa fila consultada por " +
        "responsáveis autorizados (GET /api/Usuarios/recuperacao-senha/pendentes), com deduplicação e expiração, sem notificar ninguém. " +
        "A identidade é confirmada pessoalmente pelo responsável; conhecer CPF, nascimento, telefone ou e-mail não basta. A recuperação " +
        "automática por e-mail está desabilitada (não há provedor). Exige X-CSRF-TOKEN. 400 e-mail mal formado, 429 excesso de pedidos por IP.")]
    [ProducesResponseType<RecuperacaoSenhaSolicitadaResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SolicitarRecuperacaoSenha([FromBody] SolicitarRecuperacaoSenhaRequest request, CancellationToken ct)
    {
        var ip = HttpContext.IpDeOrigem();
        using (var tentativa = limitador.RegistrarRecuperacao(ip))
        {
            if (!tentativa.IsAcquired)
            {
                return MuitasRecuperacoes(tentativa);
            }
        }

        request.Ip = ip;
        await mediator.Send(request, ct);
        return Accepted(new RecuperacaoSenhaSolicitadaResponse());
    }

    [HttpPost("recuperacao-senha/confirmar"), AllowAnonymous, ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Conclui a recuperação com a autorização entregue pelo responsável, sem login e sem senha antiga.")]
    [EndpointDescription("Corpo: token (43 caracteres, recebido pessoalmente), novaSenha e confirmacaoNovaSenha, com a mesma política da troca " +
        "autenticada. O token vale 15 minutos, uma única vez, e só enquanto a senha do titular não mudar e o responsável que o emitiu ainda " +
        "puder alterar a senha dele. Sucesso: 204; a senha muda, todas as sessões do titular são revogadas, a auditoria registra quem " +
        "autorizou e o IP de quem concluiu, e o titular recebe uma notificação interna. Não faz login: entre com a nova senha. Token " +
        "inválido, expirado, usado, revogado ou obsoleto: 400 genérico, sem alterar nada. Exige X-CSRF-TOKEN. 429 excesso de tentativas por IP.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConfirmarRecuperacaoSenha([FromBody] ConfirmarRecuperacaoSenhaRequest request, CancellationToken ct)
    {
        var ip = HttpContext.IpDeOrigem();
        using (var tentativa = limitador.RegistrarRecuperacao(ip))
        {
            if (!tentativa.IsAcquired)
            {
                return MuitasRecuperacoes(tentativa);
            }
        }

        request.Ip = ip;
        request.TraceId = HttpContext.TraceIdSeguro();
        await mediator.Send(request, ct);
        return NoContent();
    }

    private ObjectResult MuitasRecuperacoes(System.Threading.RateLimiting.RateLimitLease recusa) => RespostaMuitasTentativas.Criar(Response,
        recusa, "Muitas tentativas de recuperação de senha.", "Aguarde antes de tentar novamente ou procure um responsável autorizado do clube.");

    private ObjectResult InvalidCredentials() => Unauthorized(new ProblemDetails { Title = "Credenciais inválidas.", Status = 401 });
    private void SetRefreshCookie(string value, DateTime expires) => Response.Cookies.Append(RefreshCookie, value,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = expires });
    private void DeleteRefreshCookie() => Response.Cookies.Delete(RefreshCookie,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
}
