namespace Almirante.Api.Entities;

// Auditoria de exclusão lógica de eventos (tabela historico_eventos). Nenhum código da aplicação insere
// linhas aqui: são geradas exclusivamente pelo trigger TR_eventos_AuditoriaExclusaoLogica, na transição
// eventos.Ativo 1 -> 0, com o contexto fornecido via SESSION_CONTEXT (ver EventosService.DeleteAsync).
// O snapshot é autossuficiente (não depende de FKs para linhas mutáveis): participantes e lançamentos
// ficam em JSON. Somente leitura para a aplicação (DENY de escrita, ver DbCredentialManager).
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

    // JSON: [{ "membroId", "lancamentoId", "valor", "status" }] dos participantes ativos no instante da exclusão.
    public required string ParticipantesJson { get; set; }

    public Guid UsuarioResponsavelId { get; set; }
    public required string IpResponsavel { get; set; }
    public required string Motivo { get; set; }

    // Gerada pelo banco (SYSUTCDATETIME() no trigger).
    public DateTime ExcluidoEmUtc { get; set; }
}
