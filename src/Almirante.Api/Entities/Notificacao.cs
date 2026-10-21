namespace Almirante.Api.Entities;

/// <summary>
/// Notificação de um único destinatário, com leitura individual. As colunas Aviso* guardam o aviso em tempo real
/// ainda não emitido (outbox na própria linha), gravado na mesma transação da operação que o originou.
/// </summary>
public sealed class Notificacao
{
    public Guid Id { get; set; }
    public Guid DestinatarioId { get; set; }
    public TipoNotificacao Tipo { get; set; }
    public required string Titulo { get; set; }
    public required string Mensagem { get; set; }
    public DateTime CriadaEmUtc { get; set; }

    public Guid? EventoId { get; set; }
    public Guid? LancamentoId { get; set; }

    /// <summary>Fato que originou a notificação (ex.: "lancamento:{id}"); único por destinatário, impede duplicidade.</summary>
    public required string ChaveOrigem { get; set; }

    /// <summary>Primeira leitura confirmada pelo destinatário; nunca é sobrescrita.</summary>
    public DateTime? LidaEmUtc { get; set; }

    /// <summary>Aviso SignalR pendente de emissão; nulo quando não há nada a avisar.</summary>
    public AvisoNotificacao? AvisoPendente { get; set; }
    public int AvisoTentativas { get; set; }
    public DateTime? AvisoProximaTentativaEmUtc { get; set; }
}
