using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public class AuthService(
    AlmiranteDbContext db,
    IPasswordHasher<Usuario> passwordHasher,
    JwtTokenService tokenService)
{
    public async Task<LoginResponse?> LoginAsync(string email, string senha, CancellationToken cancellationToken)
    {
        var emailNormalizado = email.Trim().ToUpperInvariant();

        var usuario = await db.Usuarios
            .SingleOrDefaultAsync(u => u.EmailNormalizado == emailNormalizado, cancellationToken);

        if (usuario is null)
        {
            return null;
        }

        var verification = passwordHasher.VerifyHashedPassword(usuario, usuario.SenhaHash, senha);
        if (verification == PasswordVerificationResult.Failed)
        {
            return null;
        }

        var issuedToken = tokenService.GenerateToken(usuario);

        return new LoginResponse
        {
            Token = new TokenDto
            {
                AccessToken = issuedToken.AccessToken,
                ExpiresAtUtc = issuedToken.ExpiresAtUtc,
            },
            Usuario = new UsuarioDto
            {
                Id = usuario.Id,
                Nome = usuario.Nome,
                Email = usuario.Email,
                Roles = usuario.Roles,
            },
        };
    }

    public async Task<UsuarioDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var usuario = await db.Usuarios.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (usuario is null)
        {
            return null;
        }

        return new UsuarioDto
        {
            Id = usuario.Id,
            Nome = usuario.Nome,
            Email = usuario.Email,
            Roles = usuario.Roles,
        };
    }
}
