namespace Almirante.Api.Entities;

public sealed class EventoMembro
{
    public Guid Id { get; set; }
    public Guid EventoId { get; set; }
    public Evento? Evento { get; set; }
    public Guid EventoGrupoId { get; set; }
    public Guid MembroId { get; set; }
    /// <summary>Cobrança ativa do participante; nula quando o cadastro não tem valor a pagar (valor por membro zero).</summary>
    public Guid? LancamentoId { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEmUtc { get; set; }
    public DateTime? DesativadoEmUtc { get; set; }
}
