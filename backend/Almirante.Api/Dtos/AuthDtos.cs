using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Dtos;

public class LoginRequest
{
    [Required, EmailAddress]
    public required string Email { get; set; }

    [Required]
    public required string Senha { get; set; }
}

public class TokenDto
{
    public required string AccessToken { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public class UsuarioDto
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public required string Roles { get; set; }
}

public class UsuarioListItemDto
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public DateTime DataCriacao { get; set; }
    public required string Roles { get; set; }
}

public class LoginResponse
{
    public required TokenDto Token { get; set; }
    public required UsuarioDto Usuario { get; set; }
}
