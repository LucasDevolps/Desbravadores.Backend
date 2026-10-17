namespace Almirante.Api.Entities;

public sealed class EventoOperacao
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public required string IdempotencyKey { get; set; }

    public required string RequestHash { get; set; }

    public Guid EventoId { get; set; }

    public string? RespostaJson { get; set; }

    public DateTime CriadoEmUtc { get; set; }
}
