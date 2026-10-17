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

public class UsuarioDto { public Guid Id { get; set; } public required string Nome { get; set; } public required string Email { get; set; } public required CargoDto Cargo { get; set; } }
public class UsuarioListItemDto
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public string? Cpf { get; set; }
    public DateOnly? DataNascimento { get; set; }
    public string? Telefone { get; set; }
    public string? ResponsavelNome { get; set; }
    public string? ResponsavelEmail { get; set; }
    public string? ResponsavelCpf { get; set; }
    public string? ResponsavelTelefone { get; set; }
    public string? Rg { get; set; }
    public string? TipoSanguineo { get; set; }
    public string? DoencasAlergias { get; set; }
    public bool EhCin { get; set; }
    public DateOnly? DataExpiracao { get; set; }
    public EnderecoDto? Endereco { get; set; }
    public string? FotoUrl => PossuiFoto ? $"/api/Usuarios/{Id}/foto" : null;
    public DateTime DataCriacao { get; set; }
    public required string Funcao { get; set; }
    public bool Ativo { get; set; }
    public required CargoDto Cargo { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool PossuiFoto { get; set; }
}
