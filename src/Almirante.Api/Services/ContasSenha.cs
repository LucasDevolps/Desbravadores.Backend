using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Leitura, autorização e travamento das contas envolvidas numa operação de senha (issue #159). O cargo, a situação e a sessão vêm sempre do
/// banco, nunca do token ou do corpo. As verificações caras (PBKDF2) acontecem antes da transação, sobre uma fotografia das contas; dentro
/// dela, <see cref="TravarERevalidarAsync"/> trava usuários, cargos e sessão até o commit e recusa a operação se algo mudou desde a
/// fotografia. Assim um retry nunca reaproveita uma confirmação de senha feita sobre estado obsoleto.
/// </summary>
public sealed class ContasSenha(AlmiranteDbContext db, IPasswordHasher<Usuario> hasher, ILogger<ContasSenha> logger)
{
    public const string CampoSenhaAtual = "senhaAtualSolicitante";
    public const string CampoNovaSenha = "novaSenha";

    /// <summary>Fotografia de uma conta antes da transação. O hash da senha nunca aparece em ToString().</summary>
    public sealed record Conta(Guid Id, bool Ativo, string SenhaHash, long SecurityVersion, string Role, bool CargoAtivo, string Email)
    {
        public bool Utilizavel => Ativo && CargoAtivo;

        public override string ToString() => $"Conta {Id}";
    }

    /// <summary>Linha travada (UPDLOCK no usuário, HOLDLOCK no cargo) dentro da transação.</summary>
    public sealed class ContaTravada
    {
        public Guid Id { get; set; }
        public bool Ativo { get; set; }
        public long SecurityVersion { get; set; }
        public string Role { get; set; } = string.Empty;
        public bool CargoAtivo { get; set; }
    }

    public Task<Conta?> CarregarAsync(Guid id, CancellationToken ct) =>
        db.Usuarios.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new Conta(u.Id, u.Ativo, u.SenhaHash, u.SecurityVersion, u.Cargo!.Role, u.Cargo.Ativo, u.Email))
            .SingleOrDefaultAsync(ct);

    /// <summary>Solicitante ativo, com cargo ativo e a sessão do token vigente; senão 401.</summary>
    public async Task<Conta> CarregarSolicitanteAsync(ContextoSolicitanteSenha contexto, DateTime agoraUtc, CancellationToken ct)
    {
        var solicitante = await CarregarAsync(contexto.UsuarioId, ct);
        var sessaoVigente = await db.AuthSessions.AsNoTracking().Where(SessoesAutenticacao.Vigente(agoraUtc))
            .AnyAsync(s => s.Id == contexto.SessaoId && s.UsuarioId == contexto.UsuarioId, ct);
        return solicitante is { Utilizavel: true } && sessaoVigente ? solicitante : throw SessaoInvalida();
    }

    /// <summary>
    /// Solicitante e alvo de uma operação sobre outro usuário. Ordem que não permite enumeração: o cargo do solicitante (do banco) precisa
    /// atender alguém antes de qualquer consulta ao alvo; depois 404 alvo inexistente, 403 par fora da matriz e 409 alvo inativo.
    /// </summary>
    public async Task<(Conta Solicitante, Conta Alvo)> AutorizarTerceiroAsync(ContextoSolicitanteSenha contexto, Guid alvoId, DateTime agoraUtc,
        CancellationToken ct)
    {
        if (alvoId == contexto.UsuarioId)
        {
            throw ApiProblemException.Invalid("usuarioId", "Para alterar a própria senha use POST /api/Auth/alterar-senha, com a sua senha atual.");
        }

        var solicitante = await CarregarSolicitanteAsync(contexto, agoraUtc, ct);
        if (PermissoesSenha.AlvosPermitidos(solicitante.Role).Count == 0)
        {
            throw AcessoNegado();
        }

        var alvo = await CarregarAsync(alvoId, ct) ?? throw ApiProblemException.NotFound("Usuário não encontrado.");
        if (!PermissoesSenha.PodeAlterarSenhaDeTerceiro(solicitante.Role, alvo.Role))
        {
            throw AcessoNegado();
        }

        return alvo.Utilizavel ? (solicitante, alvo) : throw ApiProblemException.Conflict("Usuário inativo.",
            "A senha de um usuário inativo ou com cargo inativo não pode ser alterada. Nenhum registro foi alterado.");
    }

    /// <summary>Reautenticação pela senha atual de quem está logado, verificada contra o hash do banco; não cria sessão nem emite token.</summary>
    public void ConfirmarSenhaSolicitante(Conta solicitante, string senhaAtual)
    {
        if (hasher.VerifyHashedPassword(Usuario(solicitante), solicitante.SenhaHash, senhaAtual) == PasswordVerificationResult.Failed)
        {
            logger.LogWarning("Confirmação de senha recusada numa operação de senha do usuário {UsuarioId}.", solicitante.Id);
            throw ApiProblemException.Invalid(CampoSenhaAtual, "A senha atual informada não confere.");
        }
    }

    /// <summary>
    /// Política compartilhada com o e-mail do alvo e recusa da senha atual dele (VerifyHashedPassword, nunca comparação de hashes).
    /// Devolve o hash novo, calculado antes da transação.
    /// </summary>
    public string ValidarECalcularNovoHash(Conta alvo, string novaSenha)
    {
        var erros = PasswordPolicy.Validate(novaSenha, alvo.Email);
        if (erros.Count > 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "Dados inválidos.",
                errors: new Dictionary<string, string[]> { [CampoNovaSenha] = [.. erros] });
        }

        var usuario = Usuario(alvo);
        if (hasher.VerifyHashedPassword(usuario, alvo.SenhaHash, novaSenha) != PasswordVerificationResult.Failed)
        {
            throw ApiProblemException.Invalid(CampoNovaSenha, "A nova senha deve ser diferente da senha atual.");
        }

        return hasher.HashPassword(usuario, novaSenha);
    }

    /// <summary>
    /// Trava as contas (UPDLOCK/HOLDLOCK: login, refresh, troca de cargo ou desativação concorrentes esperam o commit) e confere que
    /// situação, cargo e SecurityVersion são os da fotografia. Com <paramref name="contexto"/>, também trava e confere a sessão do
    /// solicitante (401 se deixou de valer). Diferença → <paramref name="recusa"/> (409 nas trocas autenticadas; resposta genérica na
    /// recuperação), sem nada gravado.
    /// </summary>
    public async Task TravarERevalidarAsync(IReadOnlyCollection<Conta> contas, ContextoSolicitanteSenha? contexto, DateTime agoraUtc,
        Func<ApiProblemException> recusa, CancellationToken ct)
    {
        var ids = contas.Select(c => c.Id).Distinct().ToList();
        var primeiro = ids[0];
        var segundo = ids[^1];
        var travadas = await db.Database.SqlQuery<ContaTravada>($"""
            SELECT u.[Id], u.[Ativo], u.[SecurityVersion], c.[Role], c.[Ativo] AS [CargoAtivo]
            FROM [dbo].[Usuarios] u WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
            INNER JOIN [dbo].[Cargos] c WITH (ROWLOCK, HOLDLOCK) ON c.[Id] = u.[CargoId]
            WHERE u.[Id] IN ({primeiro}, {segundo})
            """).ToListAsync(ct);

        foreach (var conta in contas)
        {
            var atual = travadas.SingleOrDefault(t => t.Id == conta.Id);
            if (atual is null || !atual.Ativo || !atual.CargoAtivo || atual.SecurityVersion != conta.SecurityVersion
                || !string.Equals(atual.Role, conta.Role, StringComparison.Ordinal))
            {
                logger.LogWarning("Operação de senha recusada: a conta {UsuarioId} mudou durante a operação.", conta.Id);
                throw recusa();
            }
        }

        if (contexto is null)
        {
            return;
        }

        var versao = contas.Single(c => c.Id == contexto.UsuarioId).SecurityVersion;
        var sessoes = await db.Database.SqlQuery<Guid>($"""
            SELECT s.[Id] AS [Value]
            FROM [dbo].[AuthSessions] s WITH (UPDLOCK, ROWLOCK)
            WHERE s.[Id] = {contexto.SessaoId} AND s.[UsuarioId] = {contexto.UsuarioId} AND s.[RevokedAtUtc] IS NULL
              AND s.[AbsoluteExpiresAtUtc] > {agoraUtc} AND s.[SecurityVersion] = {versao}
            """).ToListAsync(ct);
        if (sessoes.Count != 1)
        {
            throw SessaoInvalida();
        }
    }

    public void ExigirSqlServer()
    {
        if (!db.Database.IsRelational())
        {
            throw new NotSupportedException("Operações de senha exigem SQL Server (transação, travas e concorrência).");
        }
    }

    public static ApiProblemException SessaoInvalida() =>
        new(StatusCodes.Status401Unauthorized, "Sessão inválida.", "Faça login novamente.");

    public static ApiProblemException AcessoNegado() =>
        new(StatusCodes.Status403Forbidden, "ACESSO NEGADO!", "Seu cargo não permite alterar a senha deste usuário.");

    public static ApiProblemException Conflito() => ApiProblemException.Conflict("A conta mudou durante a operação.",
        "Senha, cargo, situação ou sessão foram alterados ao mesmo tempo. Nenhum registro foi alterado; tente novamente.");

    /// <summary>O hasher padrão não usa o usuário; a instância só carrega o Id para implementações que usem.</summary>
    private static Usuario Usuario(Conta conta) => new() { Id = conta.Id, Nome = string.Empty, Email = conta.Email, EmailNormalizado = string.Empty, SenhaHash = conta.SenhaHash };
}
