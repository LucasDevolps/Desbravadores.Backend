namespace Almirante.Api.Entities;

/// <summary>Catálogo administrável de especialidades. Nunca é excluído fisicamente: inativar só impede novas associações.</summary>
public class Especialidade
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public string? CodigoOficial { get; set; }
    public required string Area { get; set; }
    public string? Descricao { get; set; }
    public int? NivelDificuldade { get; set; }
    public ModalidadeEspecialidade? Modalidade { get; set; }
    public string? PreRequisitosTexto { get; set; }
    public string? ReferenciaDistintivo { get; set; }
    public required string FonteOficial { get; set; }
    public string? EdicaoRevisao { get; set; }
    public bool Ativo { get; set; } = true;
    public string? MotivoInativacao { get; set; }
    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }
    public byte[] Versao { get; set; } = [];
}

/// <summary>Especialidade exigida antes de concluir outra. Removida logicamente (Ativo = 0) quando sai da lista.</summary>
public class EspecialidadePreRequisito
{
    public Guid Id { get; set; }
    public Guid EspecialidadeId { get; set; }
    public Guid PreRequisitoId { get; set; }
    public bool Ativo { get; set; } = true;
    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }
}
