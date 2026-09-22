namespace Almirante.Api.Entities;

// Participação de um membro em um cadastro de evento (tabela evento_membros). Nunca é apagada
// fisicamente: remover o participante desativa a linha (Ativo=false) e preserva o histórico.
// EventoGrupoId é replicado de Evento (FK composta garante a consistência) para que o SQL Server
// proteja, com um índice único filtrado, "um membro ativo por eventoGrupoId" entre cadastros de
// preços diferentes — mesmo sob concorrência.
public sealed class EventoMembro
{
    public Guid Id { get; set; }
    public Guid EventoId { get; set; }
    public Evento? Evento { get; set; }
    public Guid EventoGrupoId { get; set; }
    public Guid MembroId { get; set; }
    public Guid LancamentoId { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEmUtc { get; set; }
    public DateTime? DesativadoEmUtc { get; set; }
}
