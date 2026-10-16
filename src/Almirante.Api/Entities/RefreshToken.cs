namespace Almirante.Api.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public AuthSession? Session { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public RefreshToken? ReplacedByToken { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
