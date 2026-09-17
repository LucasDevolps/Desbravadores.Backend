namespace Almirante.Api.Entities;

public class Usuario
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public required string EmailNormalizado { get; set; }
    public required string SenhaHash { get; set; }
    public Guid CargoId { get; set; }
    public Cargo? Cargo { get; set; }
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;
    public long SecurityVersion { get; set; }

    // Bloqueio temporário por conta contra força bruta distribuída (ver Services/LoginLockout.cs).
    // Persistido no SQL Server, portanto compartilhado entre instâncias da API.
    public int FalhasLoginConsecutivas { get; set; }
    public DateTime? UltimaFalhaLoginUtc { get; set; }
    public DateTime? LoginBloqueadoAteUtc { get; set; }
    public ICollection<Lancamento> Lancamentos { get; set; } = [];
}
