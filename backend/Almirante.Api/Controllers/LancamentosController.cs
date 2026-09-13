using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Lancamentos")]
[Authorize]
public class LancamentosController(LancamentosService lancamentosService) : ControllerBase
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
}
