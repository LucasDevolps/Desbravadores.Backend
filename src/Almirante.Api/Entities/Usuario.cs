namespace Almirante.Api.Entities;

public class Usuario
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public required string EmailNormalizado { get; set; }

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

    public string? FotoCaminho { get; set; }

    public required string SenhaHash { get; set; }
    public Guid CargoId { get; set; }
    public Cargo? Cargo { get; set; }
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;

    public bool Ativo { get; set; } = true;
    public long SecurityVersion { get; set; }

    public int FalhasLoginConsecutivas { get; set; }
    public DateTime? UltimaFalhaLoginUtc { get; set; }
    public DateTime? LoginBloqueadoAteUtc { get; set; }
    public ICollection<Lancamento> Lancamentos { get; set; } = [];
}
