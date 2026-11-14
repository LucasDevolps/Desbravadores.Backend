using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Controllers;

/// <summary>
/// Cargos (perfis de acesso) e a administração de permissões e regras por alvo deles (issue #182). A consulta para o cadastro de usuários
/// exige cargos.consultar; o restante exige as permissões de administração de autorização (reservadas ao perfil de sistema protegido).
/// </summary>
[ApiController]
[Route("api/Cargos")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public class CargosController(CargosService cargosService, AutorizacaoAdminService admin) : ControllerBase
{
    private const string Confirmacao = " Corpo com motivo (obrigatório, até 255 caracteres, auditado) e senhaAtualSolicitante (a senha de quem " +
        "está logado; nunca registrada). Exige X-CSRF-TOKEN. 400 dados inválidos ou senha incorreta, 404 cargo inexistente, 409 versão " +
        "obsoleta (nada é sobrescrito: recarregue).";

    [HttpGet]
    [RequerPermissao(Permissoes.CargosConsultar)]
    [EndpointSummary("Lista os cargos (para o cadastro de usuários e para a tela de cargos).")]
    [EndpointDescription("Todos os cargos, ativos e inativos, com sistemaProtegido e a versao exigida nas alterações administrativas.")]
    [ProducesResponseType<IReadOnlyList<CargoDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CargoDto>>> List(CancellationToken cancellationToken) =>
        Ok(await cargosService.ListAsync(cancellationToken));

    [HttpPost]
    [RequerPermissao(Permissoes.AutorizacaoCargosGerenciar)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Cria um cargo (perfil de acesso) sem permissões nem regras por alvo.")]
    [EndpointDescription("role é normalizada para maiúsculas (2 a 20 caracteres: letras sem acento, dígitos e _, começando por letra), única e " +
        "não pode ser alterada depois; nome e descricao são obrigatórios. O cargo nasce ativo e sem nenhuma permissão: conceda-as em " +
        "PUT /api/Cargos/{id}/Permissoes." + Confirmacao + " 409 role já cadastrada (errors.role).")]
    [ProducesResponseType<CargoDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CargoDto>> Criar([FromBody] CriarCargoRequest request, CancellationToken cancellationToken)
    {
        request.Contexto = Contexto();
        var cargo = await admin.CriarCargoAsync(request, cancellationToken);
        return Created($"/api/Cargos/{cargo.Id}", cargo);
    }

    [HttpPut("{id:guid}")]
    [RequerPermissao(Permissoes.AutorizacaoCargosGerenciar)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Altera o nome e a descrição de um cargo.")]
    [EndpointDescription("Só nome, descricao e versao (a do último GET); role e sistemaProtegido não são editáveis (400 se enviados)." + Confirmacao)]
    [ProducesResponseType<CargoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CargoDto>> Atualizar(Guid id, [FromBody] AtualizarCargoRequest request, CancellationToken cancellationToken)
    {
        request.Contexto = Contexto();
        return Ok(await admin.AtualizarCargoAsync(id, request, cancellationToken));
    }

    [HttpPost("{id:guid}/Desativar")]
    [RequerPermissao(Permissoes.AutorizacaoCargosGerenciar)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Desativa um cargo (exclusão lógica).")]
    [EndpointDescription("Corpo com versao." + Confirmacao + " 409 também quando o cargo já está inativo, é o perfil de sistema protegido ou " +
        "ainda tem usuários ativos (usuariosAtivos: reatribua-os antes). Cargo inativo não concede nenhuma permissão.")]
    [ProducesResponseType<CargoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CargoDto>> Desativar(Guid id, [FromBody] SituacaoCargoRequest request, CancellationToken cancellationToken)
    {
        request.Contexto = Contexto();
        return Ok(await admin.DesativarCargoAsync(id, request, cancellationToken));
    }

    [HttpPost("{id:guid}/Reativar")]
    [RequerPermissao(Permissoes.AutorizacaoCargosGerenciar)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Reativa um cargo inativo.")]
    [EndpointDescription("Corpo com versao." + Confirmacao + " 409 também quando o cargo já está ativo. Só o cargo volta: as concessões e " +
        "regras dele continuam como estavam.")]
    [ProducesResponseType<CargoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CargoDto>> Reativar(Guid id, [FromBody] SituacaoCargoRequest request, CancellationToken cancellationToken)
    {
        request.Contexto = Contexto();
        return Ok(await admin.ReativarCargoAsync(id, request, cancellationToken));
    }

    [HttpGet("{id:guid}/Permissoes")]
    [RequerPermissao(Permissoes.AutorizacaoConsultar)]
    [EndpointSummary("Consulta as permissões concedidas a um cargo e a versão do conjunto.")]
    [ProducesResponseType<PermissoesCargoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermissoesCargoDto>> ObterPermissoes(Guid id, CancellationToken cancellationToken) =>
        Ok(await admin.ObterPermissoesAsync(id, cancellationToken));

    [HttpPut("{id:guid}/Permissoes")]
    [RequerPermissao(Permissoes.AutorizacaoConcessoesGerenciar)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Substitui de uma vez o conjunto de permissões de um cargo.")]
    [EndpointDescription("permissoes é a lista completa de chaves (GET /api/Permissoes); as ausentes são revogadas (revogação lógica, com " +
        "histórico) e tudo é aplicado numa transação, sem aplicação parcial. Vale na próxima requisição dos usuários do cargo, sem novo " +
        "login. Permissões reservadas só no perfil de sistema protegido, que não pode perdê-las (409); só se concede o que o próprio cargo " +
        "tem (403)." + Confirmacao)]
    [ProducesResponseType<PermissoesCargoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PermissoesCargoDto>> SubstituirPermissoes(Guid id, [FromBody] SubstituirPermissoesCargoRequest request,
        CancellationToken cancellationToken)
    {
        request.Contexto = Contexto();
        return Ok(await admin.SubstituirPermissoesAsync(id, request, cancellationToken));
    }

    [HttpGet("{id:guid}/RegrasAlvo")]
    [RequerPermissao(Permissoes.AutorizacaoConsultar)]
    [EndpointSummary("Consulta as regras por alvo do cargo (sobre quais cargos ele executa cada operação sensível).")]
    [EndpointDescription("Ausência de regra nega; não há hierarquia automática nem curinga.")]
    [ProducesResponseType<RegrasAlvoCargoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegrasAlvoCargoDto>> ObterRegrasAlvo(Guid id, CancellationToken cancellationToken) =>
        Ok(await admin.ObterRegrasAlvoAsync(id, cancellationToken));

    [HttpPut("{id:guid}/RegrasAlvo")]
    [RequerPermissao(Permissoes.AutorizacaoRegrasAlvoGerenciar)]
    [ValidateAntiForgeryToken]
    [ConteudoSensivel, HttpLogging(ConteudoSensivelAttribute.CamposRegistraveis)]
    [EndpointSummary("Substitui de uma vez as regras por alvo do cargo.")]
    [EndpointDescription("regras é a lista completa de { permissao, cargoAlvoId }, só com permissões que usam regra por alvo; as ausentes são " +
        "revogadas, numa transação. A regra só vale junto com a concessão da permissão ao cargo. Só se cria regra para permissão que o " +
        "próprio cargo tem e, sobre o perfil protegido, só a que ele próprio tem (403)." + Confirmacao)]
    [ProducesResponseType<RegrasAlvoCargoDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegrasAlvoCargoDto>> SubstituirRegrasAlvo(Guid id, [FromBody] SubstituirRegrasAlvoRequest request,
        CancellationToken cancellationToken)
    {
        request.Contexto = Contexto();
        return Ok(await admin.SubstituirRegrasAlvoAsync(id, request, cancellationToken));
    }

    private ContextoSolicitanteSenha Contexto() =>
        new(User.UsuarioId(), User.SessaoId(), HttpContext.IpDeOrigem(), HttpContext.TraceIdSeguro());
}

/// <summary>Catálogo técnico de permissões (issue #182), mantido por migration.</summary>
[ApiController]
[Route("api/Permissoes")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
public sealed class PermissoesController(AutorizacaoAdminService admin) : ControllerBase
{
    [HttpGet]
    [RequerPermissao(Permissoes.AutorizacaoConsultar)]
    [EndpointSummary("Lista o catálogo de permissões: chave, módulo, descrição, situação, escrita, reserva e uso de regra por alvo.")]
    [ProducesResponseType<IReadOnlyList<PermissaoDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PermissaoDto>>> Listar(CancellationToken cancellationToken) =>
        Ok(await admin.ListarPermissoesAsync(cancellationToken));
}
