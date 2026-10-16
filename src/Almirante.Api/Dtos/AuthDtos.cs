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
// Funcao: nome amigável do cargo (Cargos.Nome, ex.: "Diretor"), para exibição no lugar do código técnico (Cargo.Role).
// Cargo continua no contrato por compatibilidade com os consumidores atuais. Cpf (normalizado), DataNascimento
// (yyyy-MM-dd), Telefone e os dados do responsável vêm null quando não cadastrados; só saem em /api/Usuarios (policy
// GestaoCadastros), nunca em /Me. FotoUrl é a rota autenticada da foto (null sem foto): o caminho físico nunca sai da API.
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
    public string? FotoUrl => PossuiFoto ? $"/api/Usuarios/{Id}/foto" : null;
    public DateTime DataCriacao { get; set; }
    public required string Funcao { get; set; }
    public bool Ativo { get; set; }
    public required CargoDto Cargo { get; set; }

    // Calculado no SELECT (FotoCaminho IS NOT NULL) sem trazer o caminho; a URL é montada aqui, igual em qualquer provedor.
    [System.Text.Json.Serialization.JsonIgnore] public bool PossuiFoto { get; set; }
}
