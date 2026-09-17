using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Dtos;

public sealed class LoginRequest
{
    [Required, EmailAddress] public required string Email { get; set; }
    [Required] public required string Senha { get; set; }
}
public sealed class TokenDto { public required string AccessToken { get; set; } public DateTime ExpiresAtUtc { get; set; } }
public sealed class LoginResponse { public required TokenDto Token { get; set; } }
public sealed class MeCargoDto { public Guid Id { get; set; } public required string Nome { get; set; } public required string Role { get; set; } }
public sealed class MeDto { public Guid Id { get; set; } public required string Nome { get; set; } public required string Email { get; set; } public required MeCargoDto Cargo { get; set; } }

// DTOs administrativos permanecem separados do contrato mínimo de /Me.
public class UsuarioDto { public Guid Id { get; set; } public required string Nome { get; set; } public required string Email { get; set; } public required CargoDto Cargo { get; set; } }
public class UsuarioListItemDto { public Guid Id { get; set; } public required string Nome { get; set; } public required string Email { get; set; } public DateTime DataCriacao { get; set; } public required CargoDto Cargo { get; set; } }
