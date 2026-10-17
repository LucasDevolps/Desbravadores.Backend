namespace Almirante.Api.Entities;

public sealed class Evento
{
    public Guid Id { get; set; }

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

    public bool Ativo { get; set; } = true;

    public byte[] Versao { get; set; } = [];

    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }

    public ICollection<EventoMembro> Membros { get; set; } = [];
}
