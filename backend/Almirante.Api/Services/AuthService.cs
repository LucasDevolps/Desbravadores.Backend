using System.Security.Cryptography;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

// RefreshExpiresAtUtc tem Kind=Utc e é o prazo real da credencial (vira o Expires do cookie).
public sealed record AuthResult(LoginResponse Response, string RefreshToken, DateTime RefreshExpiresAtUtc);

public sealed class AuthService(AlmiranteDbContext db, IPasswordHasher<Usuario> passwordHasher,
    JwtTokenService tokenService, IOptions<JwtOptions> options, TimeProvider clock, ILogger<AuthService> logger)
{
    private readonly JwtOptions _options = options.Value;

    // Hash de uma senha descartável, verificado quando o e-mail não existe ou o cargo está inativo:
    // todas as tentativas de login fazem o mesmo trabalho de PBKDF2, sem atraso artificial.
    private static readonly Usuario DummyUser = new() { Nome = "", Email = "", EmailNormalizado = "", SenhaHash = "" };
    private static readonly string DummyHash = new PasswordHasher<Usuario>().HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public async Task<AuthResult?> LoginAsync(string email, string senha, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Usuarios.Include(x => x.Cargo).SingleOrDefaultAsync(x => x.EmailNormalizado == normalized, ct);
        var verified = user is null
            ? passwordHasher.VerifyHashedPassword(DummyUser, DummyHash, senha)
            : passwordHasher.VerifyHashedPassword(user, user.SenhaHash, senha);
        if (user?.Cargo is null || !user.Cargo.Ativo || verified == PasswordVerificationResult.Failed)
        {
            AuthLog.LoginFalhou(logger);
            return null;
        }
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            user.SenhaHash = passwordHasher.HashPassword(user, senha);

        var now = clock.GetUtcNow().UtcDateTime;
        var session = new AuthSession { Id = Guid.NewGuid(), UsuarioId = user.Id, CreatedAtUtc = now,
            LastRenewedAtUtc = now, AbsoluteExpiresAtUtc = now.AddDays(_options.AbsoluteSessionDays), SecurityVersion = user.SecurityVersion };
        var raw = CreateRefreshToken();
        var refresh = NewRefresh(session.Id, raw, now, session.AbsoluteExpiresAtUtc);
        db.AddRange(session, refresh);
        await db.SaveChangesAsync(ct);
        AuthLog.LoginRealizado(logger, user.Id, session.Id);
        return Issue(user.Id, user.Cargo.Role, session, raw, refresh.ExpiresAtUtc);
    }

    public async Task<AuthResult?> RefreshAsync(string rawToken, CancellationToken ct)
    {
        if (!TryHashRefreshToken(rawToken, out var hash)) return null;
        var token = await db.RefreshTokens.Include(x => x.Session)!.ThenInclude(x => x!.Usuario)!.ThenInclude(x => x!.Cargo)
            .SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (token?.Session is null) return null;
        var session = token.Session;
        var now = clock.GetUtcNow().UtcDateTime;

        if (token.ConsumedAtUtc is not null)
        {
            AuthLog.ReusoDeRefreshDetectado(logger, session.Id, session.UsuarioId);
            await RevokeSessionAsync(session.Id, "refresh-token-reuse");
            return null;
        }
        var user = session.Usuario;
        if (token.RevokedAtUtc is not null || session.RevokedAtUtc is not null || token.ExpiresAtUtc <= now ||
            session.AbsoluteExpiresAtUtc <= now || session.LastRenewedAtUtc.AddHours(_options.RefreshInactivityHours) <= now ||
            user?.Cargo is null || !user.Cargo.Ativo || user.SecurityVersion != session.SecurityVersion)
        {
            AuthLog.RefreshRecusado(logger, session.Id);
            return null;
        }

        var raw = CreateRefreshToken();
        var successor = NewRefresh(session.Id, raw, now, session.AbsoluteExpiresAtUtc);
        token.ConsumedAtUtc = now;
        token.ReplacedByTokenId = successor.Id;
        session.LastRenewedAtUtc = now;
        db.RefreshTokens.Add(successor);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // Outra renovação ou um logout alterou a sessão depois da leitura: política estrita,
            // nenhuma credencial é entregue e a família é revogada (se ainda não estiver).
            AuthLog.RenovacaoConcorrente(logger, session.Id);
            await RevokeSessionAsync(session.Id, "concurrent-refresh");
            return null;
        }
        return Issue(user.Id, user.Cargo.Role, session, raw, successor.ExpiresAtUtc);
    }

    public async Task LogoutAsync(string? rawRefresh, Guid? sessionId, Guid? userId, CancellationToken ct)
    {
        Guid? target = null;
        if (TryHashRefreshToken(rawRefresh, out var hash))
            target = await db.RefreshTokens.AsNoTracking().Where(x => x.TokenHash == hash).Select(x => (Guid?)x.SessionId).SingleOrDefaultAsync(ct);
        if (target is null && sessionId is not null && userId is not null)
            target = await db.AuthSessions.AsNoTracking().Where(x => x.Id == sessionId && x.UsuarioId == userId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (target is not null) await RevokeSessionAsync(target.Value, "logout");
    }

    // Toda revogação passa por aqui. Grava com CancellationToken.None (abortar a requisição não pode
    // desfazer a revogação) e, em conflito de rowversion com um refresh/logout concorrente, relê a
    // sessão e reaplica sobre o estado atual — revogar é idempotente.
    private async Task RevokeSessionAsync(Guid sessionId, string reason)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var session = await db.AuthSessions.SingleOrDefaultAsync(x => x.Id == sessionId, CancellationToken.None);
            if (session is null || session.RevokedAtUtc is not null) return;
            session.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
            session.RevocationReason = reason;
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                AuthLog.SessaoRevogada(logger, sessionId, reason);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts) { }
        }
    }

    private static bool TryHashRefreshToken(string? raw, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try { hash = SHA256.HashData(Base64UrlTextEncoder.Decode(raw)); return true; }
        catch (FormatException) { return false; }
    }

    public Task<MeDto?> GetMeAsync(Guid id, CancellationToken ct) => db.Usuarios.AsNoTracking().Where(x => x.Id == id && x.Cargo != null && x.Cargo.Ativo)
        .Select(x => new MeDto { Id = x.Id, Nome = x.Nome, Email = x.Email,
            Cargo = new MeCargoDto { Id = x.Cargo!.Id, Nome = x.Cargo.Nome, Role = x.Cargo.Role } }).SingleOrDefaultAsync(ct);

    private AuthResult Issue(Guid userId, string role, AuthSession session, string raw, DateTime refreshExpiry)
    {
        var jwt = tokenService.GenerateToken(userId, role, session.Id, session.AbsoluteExpiresAtUtc);
        return new(new LoginResponse { Token = new TokenDto { AccessToken = jwt.AccessToken, ExpiresAtUtc = jwt.ExpiresAtUtc } },
            raw, DateTime.SpecifyKind(refreshExpiry, DateTimeKind.Utc));
    }
    private static string CreateRefreshToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    // Cada refresh vale até a janela de inatividade, limitada pelo prazo absoluto da sessão: a rotação
    // desliza a inatividade, mas nunca estende os 7 dias. Datas lidas do SQL vêm com Kind=Unspecified.
    private RefreshToken NewRefresh(Guid sid, string raw, DateTime now, DateTime absolute)
    {
        var inactivityLimit = now.AddHours(_options.RefreshInactivityHours);
        return new()
        {
            Id = Guid.NewGuid(), SessionId = sid, TokenHash = SHA256.HashData(Base64UrlTextEncoder.Decode(raw)), CreatedAtUtc = now,
            ExpiresAtUtc = DateTime.SpecifyKind(absolute < inactivityLimit ? absolute : inactivityLimit, DateTimeKind.Utc),
        };
    }
}

// Eventos de segurança da autenticação. Nunca registram senha, tokens, cookies ou e-mail informado.
internal static partial class AuthLog
{
    [LoggerMessage(EventId = 1001, EventName = "LoginFalhou", Level = LogLevel.Information, Message = "Login recusado: credenciais inválidas ou acesso inativo.")]
    public static partial void LoginFalhou(ILogger logger);

    [LoggerMessage(EventId = 1002, EventName = "LoginRealizado", Level = LogLevel.Information, Message = "Login realizado. Usuario {UsuarioId}, sessão {SessionId}.")]
    public static partial void LoginRealizado(ILogger logger, Guid usuarioId, Guid sessionId);

    [LoggerMessage(EventId = 1003, EventName = "RefreshRecusado", Level = LogLevel.Information, Message = "Refresh recusado para a sessão {SessionId} (expirada, revogada ou acesso alterado).")]
    public static partial void RefreshRecusado(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1004, EventName = "ReusoDeRefreshDetectado", Level = LogLevel.Warning, Message = "Reuso de refresh token consumido na sessão {SessionId} do usuário {UsuarioId}; a família será revogada.")]
    public static partial void ReusoDeRefreshDetectado(ILogger logger, Guid sessionId, Guid usuarioId);

    [LoggerMessage(EventId = 1005, EventName = "RenovacaoConcorrente", Level = LogLevel.Warning, Message = "Renovação concorrente na sessão {SessionId}; a família será revogada.")]
    public static partial void RenovacaoConcorrente(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 1006, EventName = "SessaoRevogada", Level = LogLevel.Information, Message = "Sessão {SessionId} revogada. Motivo: {Motivo}.")]
    public static partial void SessaoRevogada(ILogger logger, Guid sessionId, string motivo);
}
