using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

[ApiController]
[Route("api/Cep")]
[Authorize(Policy = Policies.Autoatendimento)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public class CepController(IConsultaCep consultaCep) : ControllerBase
{
    [HttpGet("{cep}")]
    [EndpointSummary("Consulta um CEP no ViaCEP para preencher o endereço no cadastro, na edição de usuários ou nos próprios dados.")]
    [EndpointDescription("cep com 8 dígitos, com ou sem hífen (04382-160 ou 04382160). Devolve os campos do ViaCEP (cep só com dígitos, " +
        "vazios como null). Nada é gravado: o POST/PUT de /api/Usuarios e o PUT /api/Me consultam de novo e gravam. Qualquer cargo ativo (autoatendimento). 400 CEP mal formado, 404 CEP inexistente, " +
        "503 ViaCEP indisponível, lento ou com resposta inválida.")]
    [ProducesResponseType<CepDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CepDto>> Consultar(string cep, CancellationToken cancellationToken)
    {
        var normalizado = Cep.Normalizar(cep);
        if (!Cep.EhValido(normalizado))
        {
            throw ApiProblemException.Invalid("Cep", "cep inválido: informe 8 dígitos, com ou sem hífen (ex.: 04382-160).");
        }

        var resultado = await consultaCep.ConsultarAsync(normalizado!, cancellationToken);
        return Ok(resultado.ExigirEndereco(() => ApiProblemException.NotFound("CEP não encontrado.", "O ViaCEP não conhece o CEP informado."), string.Empty));
    }
}
