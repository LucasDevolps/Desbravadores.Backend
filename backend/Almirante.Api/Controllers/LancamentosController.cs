using System.Security.Claims;
using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Lancamentos")]
[Authorize]
public class LancamentosController(LancamentosService lancamentosService, LancamentosGeraisService lancamentosGeraisService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LancamentosResponse>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? tipo = null,
        [FromQuery] string? data = null,
        CancellationToken cancellationToken = default)
    {
        var response = await lancamentosService.ListAsync(page, pageSize, search, status, tipo, data, cancellationToken);
        return Ok(response);
    }

    [HttpPost]
    public async Task<ActionResult<LancamentoDto>> Create([FromBody] CreateLancamentoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await lancamentosService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(List), new { }, created);
        }
        catch (LancamentoValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Dados de lançamento inválidos.",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }
    }

    // Lançamento flexível: um único lançamento para um membro específico ou anônimo/despesa do
    // clube (membroId nulo — membroNome descreve a origem/destino, ex.: "Doação de empresário
    // local", "Compra de material de escritório"), OU o mesmo lançamento para todos os usuários
    // cadastrados (aplicarATodosOsMembros = true, reaproveitando o mesmo mecanismo idempotente do
    // lançamento geral em api/Lancamentos/Geral). Exige as mesmas roles do lançamento geral
    // (ADM/DIR/DIRA/SEC/TES, 401 "ACESSO NEGADO!" caso contrário) nos dois modos — diferente dos
    // demais endpoints deste controller, que continuam exigindo só autenticação.
    [HttpPost("Registrar")]
    [Authorize(Policy = LancamentoGeralAuthorization.PolicyName)]
    public async Task<IActionResult> Registrar(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RegistrarLancamentoRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AplicarATodosOsMembros)
        {
            return await RegistrarParaTodosOsMembrosAsync(idempotencyKey, request, cancellationToken);
        }

        return await RegistrarUnicoAsync(request, cancellationToken);
    }

    private async Task<IActionResult> RegistrarParaTodosOsMembrosAsync(
        string? idempotencyKey,
        RegistrarLancamentoRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MembroId.HasValue)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Combinação inválida.",
                Detail = "membroId não pode ser informado quando aplicarATodosOsMembros é true.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Chave de idempotência ausente.",
                Detail = "Informe o header Idempotency-Key (identificador único da requisição, diferente do token de autenticação) ao aplicar a todos os membros.",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        if (!TentarObterUsuarioAutenticado(out var usuarioId))
        {
            return AcessoNegado();
        }

        try
        {
            var resultado = await lancamentosGeraisService.CreateAsync(
                idempotencyKey.Trim(),
                new CreateLancamentoGeralRequest
                {
                    Tipo = request.Tipo,
                    Categoria = request.Categoria,
                    TipoFluxo = request.TipoFluxo,
                    Valor = request.Valor,
                    Vencimento = request.Vencimento,
                },
                usuarioId,
                cancellationToken);

            return resultado.Outcome switch
            {
                LancamentoGeralCreateOutcome.Criado => Ok(resultado.Response),
                LancamentoGeralCreateOutcome.Reutilizado => Ok(resultado.Response),
                LancamentoGeralCreateOutcome.ConflitoIdempotencia => Conflict(new ProblemDetails
                {
                    Title = "Chave de idempotência já usada com dados diferentes.",
                    Detail = "O header Idempotency-Key informado já foi usado em uma operação anterior com tipo, categoria, tipo de fluxo, valor ou vencimento diferentes.",
                    Status = StatusCodes.Status409Conflict,
                }),
                _ => throw new InvalidOperationException($"Resultado inesperado: {resultado.Outcome}."),
            };
        }
        catch (LancamentoValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Dados de lançamento inválidos.",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }
    }

    private async Task<IActionResult> RegistrarUnicoAsync(RegistrarLancamentoRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MembroNome))
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Dados de lançamento inválidos.",
                Detail = "membroNome é obrigatório quando aplicarATodosOsMembros é false (nome do membro, do doador/contribuinte, ou descrição da despesa).",
                Status = StatusCodes.Status400BadRequest,
            });
        }

        try
        {
            var created = await lancamentosService.CreateAsync(new CreateLancamentoRequest
            {
                MembroId = request.MembroId,
                MembroNome = request.MembroNome,
                Tipo = request.Tipo,
                Descricao = request.Descricao,
                Categoria = request.Categoria,
                TipoFluxo = request.TipoFluxo,
                Valor = request.Valor,
                Moeda = request.Moeda,
                Vencimento = request.Vencimento,
                Status = request.Status,
            }, cancellationToken);

            return CreatedAtAction(nameof(List), new { }, created);
        }
        catch (LancamentoValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Dados de lançamento inválidos.",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LancamentoDto>> Update(Guid id, [FromBody] UpdateLancamentoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await lancamentosService.UpdateAsync(id, request, cancellationToken);
            if (updated is null)
            {
                return NotFound();
            }

            return Ok(updated);
        }
        catch (LancamentoValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Title = "Dados de lançamento inválidos.",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest,
            });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await lancamentosService.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
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
