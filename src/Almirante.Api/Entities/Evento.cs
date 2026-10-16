namespace Almirante.Api.Entities;

// Cadastro/grupo de cobrança de um passeio (tabela eventos). Cada POST /api/Eventos cria um Evento com
// uma composição de custos e uma lista de participantes; cadastros do mesmo passeio compartilham
// EventoGrupoId (definido pelo servidor a partir de EventoReferenciaId, nunca por data/local).
// Valores já estão NORMALIZADOS: componente ignorado por ehGratis/individual é gravado como zero.
public sealed class Evento
{
    public Guid Id { get; set; }

    // Primeiro cadastro do passeio: EventoGrupoId == Id e EventoReferenciaId nulo.
    public Guid EventoGrupoId { get; set; }
    public Guid? EventoReferenciaId { get; set; }

    public DateOnly DataEvento { get; set; }
    public required string Local { get; set; }

    public bool TransporteEhGratis { get; set; }
    public decimal TransporteValor { get; set; }
    public bool AlimentacaoIndividual { get; set; }
    public decimal AlimentacaoValor { get; set; }
    public decimal SeguroObrigatorio { get; set; }

    // transporte efetivo + alimentação efetiva + seguro. O total é derivado (× participantes ativos).
    public decimal ValorPorMembro { get; set; }

    public bool Ativo { get; set; } = true;

    // Controle otimista (rowversion). Qualquer UPDATE do cadastro, inclusive a mudança de status de um
    // lançamento do evento (ver LancamentosService.UpdateAsync), altera este valor.
    public byte[] Versao { get; set; } = [];

    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }

    public ICollection<EventoMembro> Membros { get; set; } = [];
}
