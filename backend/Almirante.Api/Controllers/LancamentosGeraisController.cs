using System.Security.Claims;
using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

// Lançamento geral: cria o mesmo lançamento para todos os usuários elegíveis de uma vez (issue
// #14). Escopo separado do CRUD genérico em LancamentosController — rotas, autorização (só as
// roles de LancamentoGeralAuthorization.RolesPermitidas, com 401 "ACESSO NEGADO!" tanto para
// requisições não autenticadas quanto para autenticadas sem role permitida) e exclusão (lógica,
// nunca física) são exclusivos deste escopo e não alteram o comportamento de
// LancamentosController.
[ApiController]
[Route("api/Lancamentos/Geral")]
[Authorize(Policy = LancamentoGeralAuthorization.PolicyName)]
public class LancamentosGeraisController(LancamentosGeraisService lancamentosGeraisService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<LancamentoGeralResponse>> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateLancamentoGeralRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Chave de idempotência ausente.",
                Detail = "Informe o header Idempotency-Key (identificador único da requisição, diferente do token de autenticação).",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (!TentarObterUsuarioAutenticado(out var usuarioId))
        {
            return AcessoNegado();
        }

        try
        {
            var resultado = await lancamentosGeraisService.CreateAsync(idempotencyKey.Trim(), request, usuarioId, cancellationToken);

            return resultado.Outcome switch
            {
                LancamentoGeralCreateOutcome.Criado => Ok(resultado.Response),
                LancamentoGeralCreateOutcome.Reutilizado => Ok(resultado.Response),
                LancamentoGeralCreateOutcome.ConflitoIdempotencia => Conflict(new ProblemDetails
                {
                    Title = "Chave de idempotência já usada com dados diferentes.",
                    Detail = "O header Idempotency-Key informado já foi usado em uma operação anterior com tipo, categoria, valor ou vencimento diferentes.",
                    Status = StatusCodes.Status409Conflict,
                }),
                _ => throw new InvalidOperationException($"Resultado inesperado: {resultado.Outcome}."),
            };
        }
        catch (LancamentoValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Dados de lançamento geral inválidos.",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }
    }

    [HttpGet]
    public async Task<ActionResult<LancamentosGeralListResponse>> List(
        [FromQuery] string? periodo,
        [FromQuery] string? dataInicio,
        [FromQuery] string? dataFim,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await lancamentosGeraisService.ListAsync(periodo, dataInicio, dataFim, cancellationToken);
            return Ok(response);
        }
        catch (LancamentoValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Filtro de período inválido.",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromBody] DeleteLancamentoGeralRequest request,
        CancellationToken cancellationToken)
    {
        var motivo = request.Motivo.Trim();
        if (motivo.Length is 0 or > 255)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Motivo inválido.",
                Detail = "Motivo é obrigatório e deve ter entre 1 e 255 caracteres após remover espaços das extremidades.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (!TentarObterUsuarioAutenticado(out var usuarioId))
        {
            return AcessoNegado();
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

        var outcome = await lancamentosGeraisService.DeleteAsync(id, motivo, usuarioId, ip, cancellationToken);

        return outcome switch
        {
            LancamentoGeralDeleteOutcome.Excluido => NoContent(),
            LancamentoGeralDeleteOutcome.NaoEncontrado => NotFound(),
            _ => throw new InvalidOperationException($"Resultado inesperado: {outcome}."),
        };
    }

    private bool TentarObterUsuarioAutenticado(out Guid usuarioId)
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out usuarioId);
    }

    private UnauthorizedObjectResult AcessoNegado() => Unauthorized(new ProblemDetails
    {
        Title = "ACESSO NEGADO!",
        Status = StatusCodes.Status401Unauthorized,
    });
}
