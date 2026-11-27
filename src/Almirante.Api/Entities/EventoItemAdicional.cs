namespace Almirante.Api.Entities;

public sealed class EventoItemAdicional
{
    public Guid Id { get; set; }
    public Guid EventoId { get; set; }
    public int Ordem { get; set; }
    public required string Titulo { get; set; }
    public decimal Valor { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEmUtc { get; set; }
    public DateTime? DesativadoEmUtc { get; set; }
}
