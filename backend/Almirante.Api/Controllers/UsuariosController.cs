using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

// Controller fino (mesmo desenho de EventosController): autorização, dados que só o servidor fornece (responsável
// das claims, IP da conexão, papel do solicitante) e envio ao MediatR. Nenhuma regra de negócio aqui.
[ApiController]
[Route("api/Usuarios")]
[Authorize(Policy = Policies.GestaoCadastros)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public class UsuariosController(UsuariosService usuariosService, ISender mediator) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Lista os usuários; por padrão, somente os ativos.")]
    [EndpointDescription("includeInactive=true inclui os usuários excluídos logicamente. funcao traz o nome do cargo (ex.: \"Diretor\"). " +
        "cpf (normalizado), dataNascimento (yyyy-MM-dd) e telefone vêm null quando não cadastrados.")]
    [ProducesResponseType<IReadOnlyList<UsuarioListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UsuarioListItemDto>>> List(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var usuarios = await usuariosService.ListAsync(includeInactive, cancellationToken);
        return Ok(usuarios);
    }

    [HttpPost]
    [EndpointSummary("Cadastra um usuário.")]
    [EndpointDescription("E-mail único sem diferenciar maiúsculas/minúsculas (409 se já existir, inclusive entre inativos). " +
        "cpf, dataNascimento (yyyy-MM-dd, não futura) e telefone são opcionais; cpf é único entre todos os usuários, comparado sem " +
        "pontos, hífen e espaços externos (409 se já existir). " +
        "cargoId deve ser um cargo ativo; somente um Administrador cadastra outro Administrador (403).")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Registrar([FromBody] RegistrarUsuarioRequest request, CancellationToken cancellationToken)
    {
        request.SolicitanteEhAdmin = User.IsInRole(Roles.Admin);
        var usuario = await mediator.Send(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, usuario);
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Altera nome, e-mail, cargo e dados pessoais de um usuário ativo (estado anterior completo auditado em _usuarios_hist).")]
    [EndpointDescription("nome, email e cargoId são obrigatórios e substituídos. cpf, dataNascimento e telefone omitidos mantêm o valor atual; " +
        "null (ou \"\" em cpf/telefone) remove. 409 se o e-mail ou o CPF pertencer a outro usuário ou se o usuário estiver inativo; 404 se inexistente.")]
    [ProducesResponseType<UsuarioListItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUsuarioRequest request, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        request.Id = id;
        request.UsuarioResponsavelId = usuarioId;
        request.IpResponsavel = HttpContext.IpDeOrigem();
        request.SolicitanteEhAdmin = User.IsInRole(Roles.Admin);
        return Ok(await mediator.Send(request, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Exclui logicamente (inativa) um usuário (estado anterior auditado em _usuarios_hist).")]
    [EndpointDescription("409 se o usuário estiver vinculado a eventos de hoje em diante (extensão eventos lista todos), se já estiver inativo " +
        "ou se for o próprio solicitante; 404 se inexistente. Eventos passados não impedem a exclusão.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TentarObterUsuarioId(out var usuarioId))
        {
            return IdentidadeInvalida();
        }

        await mediator.Send(new DeleteUsuarioRequest(id, usuarioId, HttpContext.IpDeOrigem(), User.IsInRole(Roles.Admin)), cancellationToken);
        return NoContent();
    }

    // Inalcançável com o pipeline atual (OnTokenValidated exige sub GUID), mantido como defesa.
    private UnauthorizedObjectResult IdentidadeInvalida() => Unauthorized(new ProblemDetails
    {
        Title = "Identidade autenticada inválida.",
        Status = StatusCodes.Status401Unauthorized,
    });
}
