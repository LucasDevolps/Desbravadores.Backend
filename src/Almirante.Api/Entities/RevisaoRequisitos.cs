namespace Almirante.Api.Entities;

/// <summary>
/// Conjunto de requisitos de uma classe ou especialidade numa edição identificável. Os requisitos são imutáveis (trigger): uma
/// mudança oficial gera nova revisão, e quem já usa a anterior continua nela até uma migração explícita.
/// </summary>
public class RevisaoRequisitos
{
    public Guid Id { get; set; }
    public ClasseDesbravador? Classe { get; set; }
    public Guid? EspecialidadeId { get; set; }
    public required string Edicao { get; set; }
    public required string FonteOficial { get; set; }
    public string? Observacoes { get; set; }
    public SituacaoRevisao Situacao { get; set; } = SituacaoRevisao.Rascunho;
    public string? MotivoDescarte { get; set; }
    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? PublicadaPorUsuarioId { get; set; }
    public DateTime? PublicadaEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }
    public byte[] Versao { get; set; } = [];
    public ICollection<Requisito> Requisitos { get; set; } = [];
}

/// <summary>
/// Requisito ou grupo de requisitos. Sem <see cref="Regra"/> é um item a validar; com regra, é satisfeito pelos subitens
/// (todos os obrigatórios, ou ao menos <see cref="QuantidadeMinima"/> alternativas). <see cref="Obrigatorio"/> só vale na raiz e
/// em grupos "Todos"; opções de um grupo de alternativas nunca são obrigatórias.
/// </summary>
public class Requisito
{
    public Guid Id { get; set; }
    public Guid RevisaoId { get; set; }
    public Guid? RequisitoPaiId { get; set; }
    public required string Codigo { get; set; }
    public string? Secao { get; set; }
    public int Ordem { get; set; }
    public required string Descricao { get; set; }
    public bool Obrigatorio { get; set; }
    public RegraGrupo? Regra { get; set; }
    public int? QuantidadeMinima { get; set; }
    public string? ReferenciaOficial { get; set; }

    /// <summary>Quando informado, o item exige essa quantidade de especialidades concluídas vinculadas como evidência.</summary>
    public int? EspecialidadesExigidas { get; set; }
    public Guid? EspecialidadeExigidaId { get; set; }
    public string? AreaEspecialidadeExigida { get; set; }

    /// <summary>A especialidade precisa ter sido concluída a partir do início da classe e não pode ser evidência de outra classe.</summary>
    public bool SomenteEspecialidadeNaoRealizada { get; set; }
}
