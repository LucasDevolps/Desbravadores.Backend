namespace Almirante.Api.Entities;

public class AuthSession
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public Usuario? Usuario { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastRenewedAtUtc { get; set; }
    public DateTime AbsoluteExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }
    public long SecurityVersion { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
