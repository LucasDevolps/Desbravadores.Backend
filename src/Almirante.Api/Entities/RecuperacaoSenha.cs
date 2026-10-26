using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<OrigemRecuperacaoSenha>))]
public enum OrigemRecuperacaoSenha
{
    /// <summary>Pedido anônimo em POST /api/Auth/recuperacao-senha/solicitar.</summary>
    SolicitacaoPublica = 1,

    /// <summary>Criada pelo responsável ao emitir a autorização para quem procurou o clube sem pedido prévio.</summary>
    Atendimento = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<StatusRecuperacaoSenha>))]
public enum StatusRecuperacaoSenha
{
    Pendente = 1,
    Autorizada = 2,
    Concluida = 3,
    Invalidada = 4,
}

/// <summary>
/// Como o responsável confirmou a identidade do titular antes de autorizar. A declaração registra um atendimento humano; não é uma prova
/// verificada pelo backend. Conferir CPF, nascimento, telefone ou e-mail ajuda a localizar o cadastro, mas não é método aceito.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetodoVerificacaoIdentidade>))]
public enum MetodoVerificacaoIdentidade
{
    /// <summary>Titular atendido pessoalmente pelo responsável.</summary>
    AtendimentoPresencial = 1,

    /// <summary>Procedimento de atendimento independente já estabelecido pelo clube (ex.: confirmação pelo responsável legal presencialmente).</summary>
    ProcedimentoIndependenteEstabelecido = 2,
}

/// <summary>
/// Pedido de recuperação de senha e, depois da verificação humana, a autorização de uso único (tabela Usuarios_recuperacoes_senha). Ciclo de
/// vida próprio, separado da auditoria imutável: Pendente → Autorizada → Concluida, ou Invalidada (expirou, a senha mudou por outro fluxo, o
/// autorizador perdeu a permissão). No máximo uma aberta (Pendente ou Autorizada) por usuário. Do token só o hash SHA-256 é guardado.
/// </summary>
public sealed class RecuperacaoSenha
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public OrigemRecuperacaoSenha Origem { get; set; }
    public StatusRecuperacaoSenha Status { get; set; }

    public DateTime SolicitadaEmUtc { get; set; }
    public DateTime UltimaSolicitacaoEmUtc { get; set; }

    /// <summary>Pedidos públicos repetidos agrupados neste registro (deduplicação).</summary>
    public int Solicitacoes { get; set; }

    /// <summary>IP observado do pedido público (nulo na origem atendimento).</summary>
    public string? IpSolicitacao { get; set; }

    /// <summary>Até quando o registro aberto aparece nas pendências; depois disso é encerrado como expirado.</summary>
    public DateTime ExpiraEmUtc { get; set; }

    public Guid? UsuarioAutorizadorId { get; set; }
    public string? IpAutorizador { get; set; }
    public MetodoVerificacaoIdentidade? MetodoVerificacao { get; set; }
    public DateTime? AutorizadaEmUtc { get; set; }

    /// <summary>SHA-256 do token com separação de finalidade; o token em claro só aparece uma vez, na resposta da emissão.</summary>
    public byte[]? TokenHash { get; set; }
    public DateTime? TokenExpiraEmUtc { get; set; }

    /// <summary>SecurityVersion do titular na emissão: qualquer troca de senha depois disso torna o token obsoleto.</summary>
    public long? SecurityVersion { get; set; }

    public DateTime? EncerradaEmUtc { get; set; }

    /// <summary>IP de quem concluiu com o token (sem identidade autenticada).</summary>
    public string? IpConclusao { get; set; }

    public string? MotivoEncerramento { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
