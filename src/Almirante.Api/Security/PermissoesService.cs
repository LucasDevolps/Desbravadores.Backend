using System.Collections.Concurrent;
using Almirante.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Security;

/// <summary>Permissões efetivas de um usuário: as do cargo e, se designado responsável pelo almoxarifado, as da designação.</summary>
public sealed record PermissoesEfetivas(Guid? CargoId, IReadOnlySet<string> DoCargo, bool ResponsavelAlmoxarifado)
{
    public static readonly PermissoesEfetivas Nenhuma = new(null, new HashSet<string>(StringComparer.Ordinal), false);

    public IReadOnlyList<string> PorDesignacao => ResponsavelAlmoxarifado ? CatalogoPermissoes.PorDesignacaoAlmoxarifado : [];

    public bool Tem(string chave) => DoCargo.Contains(chave) || PorDesignacao.Contains(chave, StringComparer.Ordinal);

    public IReadOnlyList<string> Todas => [.. DoCargo.Union(PorDesignacao, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// Decisões de autorização lidas do banco (issue #182). Escopo da requisição: as permissões de um usuário são lidas uma vez por requisição e
/// nunca guardadas entre requisições, então uma concessão revogada deixa de valer na requisição seguinte, com o mesmo token. Permissão,
/// concessão, regra ou cargo inativos nunca concedem; chave desconhecida nunca vira "somente autenticado".
/// </summary>
public sealed class PermissoesService(AlmiranteDbContext db)
{
    private readonly Dictionary<Guid, PermissoesEfetivas> _porUsuario = [];

    public async Task<PermissoesEfetivas> DoUsuarioAsync(Guid usuarioId, CancellationToken ct)
    {
        if (_porUsuario.TryGetValue(usuarioId, out var memorizadas))
        {
            return memorizadas;
        }

        var cargoId = await db.Usuarios.AsNoTracking()
            .Where(u => u.Id == usuarioId && u.Ativo && u.Cargo!.Ativo)
            .Select(u => (Guid?)u.CargoId)
            .SingleOrDefaultAsync(ct);
        var efetivas = cargoId is null
            ? PermissoesEfetivas.Nenhuma
            : new PermissoesEfetivas(cargoId, await DoCargoAsync(cargoId.Value, ct),
                await db.ResponsaveisAlmoxarifado.AsNoTracking().AnyAsync(r => r.MembroId == usuarioId && r.RevogadoEmUtc == null, ct));
        _porUsuario[usuarioId] = efetivas;
        return efetivas;
    }

    public async Task<bool> TemAsync(System.Security.Claims.ClaimsPrincipal usuario, string chave, CancellationToken ct) =>
        usuario.TentarObterUsuarioId(out var id) && (await DoUsuarioAsync(id, ct)).Tem(chave);

    /// <summary>Concessões ativas de permissões ativas do cargo (sem conferir se o cargo está ativo).</summary>
    public async Task<IReadOnlySet<string>> DoCargoAsync(Guid cargoId, CancellationToken ct) =>
        (await db.CargoPermissoes.AsNoTracking()
            .Where(cp => cp.CargoId == cargoId && cp.Ativo && cp.Permissao!.Ativo)
            .Select(cp => cp.Permissao!.Chave)
            .ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Se quem tem o cargo <paramref name="roleSolicitante"/> pode executar <paramref name="chave"/> sobre usuários do cargo
    /// <paramref name="roleAlvo"/>: cargo solicitante ativo, concessão ativa da permissão e regra ativa para o par. Ausência de regra nega.
    /// </summary>
    public Task<bool> PodeSobreAlvoAsync(string roleSolicitante, string chave, string roleAlvo, CancellationToken ct) =>
        RegrasVigentes(chave)
            .AnyAsync(r => r.CargoSolicitante!.Role == roleSolicitante && r.CargoAlvo!.Role == roleAlvo, ct);

    /// <summary>Roles alvo da operação para o cargo solicitante (vazio sem a concessão).</summary>
    public async Task<IReadOnlyList<string>> AlvosPermitidosAsync(string roleSolicitante, string chave, CancellationToken ct) =>
        await RegrasVigentes(chave)
            .Where(r => r.CargoSolicitante!.Role == roleSolicitante)
            .Select(r => r.CargoAlvo!.Role)
            .Distinct()
            .ToListAsync(ct);

    /// <summary>Se algum cargo ativo, com a concessão, pode executar a operação sobre usuários do cargo alvo.</summary>
    public Task<bool> AlguemPodeSobreAsync(string roleAlvo, string chave, CancellationToken ct) =>
        RegrasVigentes(chave).AnyAsync(r => r.CargoAlvo!.Role == roleAlvo, ct);

    private IQueryable<Entities.CargoRegraAlvo> RegrasVigentes(string chave) =>
        db.CargoRegrasAlvo.AsNoTracking().Where(r => r.Ativo && r.Permissao!.Ativo && r.Permissao.Chave == chave && r.CargoSolicitante!.Ativo
            && db.CargoPermissoes.Any(cp => cp.CargoId == r.CargoSolicitanteId && cp.PermissaoId == r.PermissaoId && cp.Ativo));

    /// <summary>
    /// Dentro da transação de uma operação sensível (senhas, cadastro e foto de terceiros), antes de travar usuários e cargos: trava
    /// compartilhada na revisão global até o commit. Uma alteração administrativa de autorização (que trava a revisão com exclusividade
    /// antes de qualquer outra escrita) espera esta operação terminar, e esta nunca decide com uma matriz que já foi substituída.
    /// </summary>
    public async Task TravarRevisaoParaLeituraAsync(CancellationToken ct)
    {
        _porUsuario.Clear();
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT [Revisao] FROM [dbo].[{AlmiranteDbContext.TabelaAutorizacaoEstado}] WITH (HOLDLOCK, ROWLOCK) WHERE [Id] = 1", ct);
        }
    }

    /// <summary>Descarta as permissões memorizadas nesta requisição (depois de uma alteração administrativa).</summary>
    public void Esquecer() => _porUsuario.Clear();
}

/// <summary>Exige a permissão <see cref="Chave"/> do usuário autenticado, conferida no banco a cada requisição.</summary>
public sealed class PermissaoRequirement(string chave) : IAuthorizationRequirement
{
    public string Chave { get; } = chave;
}

public sealed class PermissaoAuthorizationHandler(PermissoesService permissoes) : AuthorizationHandler<PermissaoRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissaoRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // Autenticado sem id de usuário: 401 "Identidade autenticada inválida." (como nos controllers), não 403 de permissão.
        var usuarioId = context.User.UsuarioId();
        var ct = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;
        if ((await permissoes.DoUsuarioAsync(usuarioId, ct)).Tem(requirement.Chave))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Interpreta só as policies <c>perm:&lt;chave&gt;</c>, criando um <see cref="PermissaoRequirement"/>; as demais (e a default/fallback) vão ao
/// provedor padrão. Não guarda concessões nem usa o DbContext: a decisão é do handler, a cada requisição.
/// </summary>
public sealed class PermissaoPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefixo = "perm:";

    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _policies = new(StringComparer.Ordinal);

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
        policyName.StartsWith(Prefixo, StringComparison.Ordinal)
            ? Task.FromResult<AuthorizationPolicy?>(_policies.GetOrAdd(policyName, static nome => new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissaoRequirement(nome[Prefixo.Length..]))
                .Build()))
            : base.GetPolicyAsync(policyName);
}

/// <summary>Exige uma permissão do catálogo (<see cref="Permissoes"/>). Use uma por action; não acumule com policies no controller.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequerPermissaoAttribute(string chave) : AuthorizeAttribute(PermissaoPolicyProvider.Prefixo + chave)
{
    public string Chave { get; } = chave;
}
