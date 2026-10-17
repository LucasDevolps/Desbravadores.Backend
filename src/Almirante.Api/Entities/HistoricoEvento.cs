namespace Almirante.Api.Entities;

public sealed class HistoricoEvento
{
    public Guid Id { get; set; }
    public Guid EventoId { get; set; }
    public Guid EventoGrupoId { get; set; }
    public Guid? EventoReferenciaId { get; set; }
    public DateOnly DataEvento { get; set; }
    public required string Local { get; set; }
    public bool TransporteEhGratis { get; set; }
    public decimal TransporteValor { get; set; }
    public bool AlimentacaoIndividual { get; set; }
    public decimal AlimentacaoValor { get; set; }
    public decimal SeguroObrigatorio { get; set; }
    public decimal ValorPorMembro { get; set; }
    public int QuantidadeMembros { get; set; }
    public decimal Total { get; set; }

    public required string ParticipantesJson { get; set; }

    public Guid UsuarioResponsavelId { get; set; }
    public required string IpResponsavel { get; set; }
    public required string Motivo { get; set; }

    public DateTime ExcluidoEmUtc { get; set; }
}
