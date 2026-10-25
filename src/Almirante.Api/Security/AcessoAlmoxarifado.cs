using Almirante.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Security;

/// <summary>Gestão (<see cref="Roles.Almoxarifado"/>) ou consulta (todo cargo fora de <see cref="Roles.SemConsultaAlmoxarifado"/>) do almoxarifado.</summary>
public sealed class AcessoAlmoxarifadoRequirement : IAuthorizationRequirement
{
    private AcessoAlmoxarifadoRequirement(bool somenteGestao) => SomenteGestao = somenteGestao;

    public static AcessoAlmoxarifadoRequirement Gestao { get; } = new(true);

    public static AcessoAlmoxarifadoRequirement Consulta { get; } = new(false);

    public bool SomenteGestao { get; }

    /// <summary>Decisão só pelo cargo; sem ela, ainda vale a designação de responsável pelo almoxarifado.</summary>
    public bool PermitidoPeloCargo(string role) => role.Length > 0 && (SomenteGestao
        ? Roles.Almoxarifado.Contains(role)
        : !Roles.SemConsultaAlmoxarifado.Contains(role));
}

/// <summary>
/// Aceita pelo cargo do token (já conferido com o banco em OnTokenValidated) ou pela designação vigente do usuário como responsável pelo
/// almoxarifado, lida do banco a cada requisição: designar ou revogar vale na requisição seguinte, sem novo login.
/// </summary>
public sealed class AcessoAlmoxarifadoHandler(AlmiranteDbContext db) : AuthorizationHandler<AcessoAlmoxarifadoRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AcessoAlmoxarifadoRequirement requirement)
    {
        if (requirement.PermitidoPeloCargo(context.User.Role()))
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.TentarObterUsuarioId(out var usuarioId)
            && await db.ResponsaveisAlmoxarifado.AsNoTracking().AnyAsync(r => r.MembroId == usuarioId && r.RevogadoEmUtc == null))
        {
            context.Succeed(requirement);
        }
    }
}
