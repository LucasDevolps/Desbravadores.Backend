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
    JwtTokenService tokenService, IOptions<JwtOptions> options, TimeProvider clock, LoginLockout lockout)
{
    private readonly JwtOptions _options = options.Value;

    // Hash de uma senha aleatória, verificado quando o e-mail não existe, para que esse caminho custe
    // o mesmo PBKDF2 de uma senha errada e o tempo de resposta não revele contas existentes.
    private static readonly Usuario DummyUser = new() { Nome = "", Email = "", EmailNormalizado = "", SenhaHash = "" };
    private static string? _dummyHash;

    public async Task<AuthResult?> LoginAsync(string email, string senha, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Usuarios.Include(x => x.Cargo).SingleOrDefaultAsync(x => x.EmailNormalizado == normalized, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (user is null)
        {
            _dummyHash ??= passwordHasher.HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            passwordHasher.VerifyHashedPassword(DummyUser, _dummyHash, senha);
            return null;
        }

        // A senha é sempre verificada (mesmo bloqueada ou com cargo inativo) para manter o custo
        // constante; a resposta ao chamador é a mesma em todos os casos de falha.
        var verified = passwordHasher.VerifyHashedPassword(user, user.SenhaHash, senha);
        if (LoginLockout.EstaBloqueado(user, now)) return null;
        if (verified == PasswordVerificationResult.Failed)
        {
            await lockout.RegistrarFalhaAsync(user, now, ct);
            return null;
        }
        if (user.Cargo is null || !user.Cargo.Ativo) return null;
        LoginLockout.RegistrarSucesso(user);
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            user.SenhaHash = passwordHasher.HashPassword(user, senha);

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
            if (session.RevokedAtUtc is null) { session.RevokedAtUtc = now; session.RevocationReason = "refresh-token-reuse"; await db.SaveChangesAsync(ct); }
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
            db.ChangeTracker.Clear();
            var raced = await db.AuthSessions.SingleAsync(x => x.Id == session.Id, ct);
            if (raced.RevokedAtUtc is null) { raced.RevokedAtUtc = now; raced.RevocationReason = "concurrent-refresh"; await db.SaveChangesAsync(ct); }
            return null;
        }
        return Issue(user.Id, user.Cargo.Role, session, raw, successor.ExpiresAtUtc);
    }

    public async Task LogoutAsync(string? rawRefresh, Guid? sessionId, Guid? userId, CancellationToken ct)
    {
        AuthSession? session = null;
        if (!string.IsNullOrWhiteSpace(rawRefresh))
        {
            try { var hash = SHA256.HashData(Base64UrlTextEncoder.Decode(rawRefresh)); session = await db.RefreshTokens.Where(x => x.TokenHash == hash).Select(x => x.Session).SingleOrDefaultAsync(ct); }
            catch (FormatException) { }
        }
        if (session is null && sessionId is not null && userId is not null)
            session = await db.AuthSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.UsuarioId == userId, ct);
        if (session is not null && session.RevokedAtUtc is null) { session.RevokedAtUtc = clock.GetUtcNow().UtcDateTime; session.RevocationReason = "logout"; await db.SaveChangesAsync(ct); }
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
