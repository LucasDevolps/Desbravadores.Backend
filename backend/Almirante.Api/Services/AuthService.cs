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

public sealed record AuthResult(LoginResponse Response, string RefreshToken, DateTime RefreshExpiresAtUtc);

public sealed class AuthService(AlmiranteDbContext db, IPasswordHasher<Usuario> passwordHasher,
    JwtTokenService tokenService, IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public async Task<AuthResult?> LoginAsync(string email, string senha, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Usuarios.Include(x => x.Cargo).SingleOrDefaultAsync(x => x.EmailNormalizado == normalized, ct);
        if (user is null || user.Cargo is null || !user.Cargo.Ativo) return null;
        var verified = passwordHasher.VerifyHashedPassword(user, user.SenhaHash, senha);
        if (verified == PasswordVerificationResult.Failed) return null;
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            user.SenhaHash = passwordHasher.HashPassword(user, senha);

        var now = clock.GetUtcNow().UtcDateTime;
        var session = new AuthSession { Id = Guid.NewGuid(), UsuarioId = user.Id, CreatedAtUtc = now,
            LastRenewedAtUtc = now, AbsoluteExpiresAtUtc = now.AddDays(_options.AbsoluteSessionDays), SecurityVersion = user.SecurityVersion };
        var raw = CreateRefreshToken();
        var refresh = NewRefresh(session.Id, raw, now, session.AbsoluteExpiresAtUtc);
        db.AddRange(session, refresh);
        await db.SaveChangesAsync(ct);
        return Issue(user.Id, user.Cargo.Role, session, raw, refresh.ExpiresAtUtc);
    }

    public async Task<AuthResult?> RefreshAsync(string rawToken, CancellationToken ct)
    {
        var hash = SHA256.HashData(Base64UrlTextEncoder.Decode(rawToken));
        var token = await db.RefreshTokens.Include(x => x.Session)!.ThenInclude(x => x!.Usuario)!.ThenInclude(x => x!.Cargo)
            .SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (token?.Session is null) return null;
        var session = token.Session;
        var now = clock.GetUtcNow().UtcDateTime;

        if (token.ConsumedAtUtc is not null)
        {
            await RevokeSessionAsync(session.Id, "refresh-token-reuse");
            return null;
        }
        var user = session.Usuario;
        if (token.RevokedAtUtc is not null || session.RevokedAtUtc is not null || token.ExpiresAtUtc <= now ||
            session.AbsoluteExpiresAtUtc <= now || session.LastRenewedAtUtc.AddHours(_options.RefreshInactivityHours) <= now ||
            user?.Cargo is null || !user.Cargo.Ativo || user.SecurityVersion != session.SecurityVersion) return null;

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
        return new(new LoginResponse { Token = new TokenDto { AccessToken = jwt.AccessToken, ExpiresAtUtc = jwt.ExpiresAtUtc } }, raw, refreshExpiry);
    }
    private static string CreateRefreshToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static RefreshToken NewRefresh(Guid sid, string raw, DateTime now, DateTime absolute) => new()
    { Id = Guid.NewGuid(), SessionId = sid, TokenHash = SHA256.HashData(Base64UrlTextEncoder.Decode(raw)), CreatedAtUtc = now, ExpiresAtUtc = absolute };
}
