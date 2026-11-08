namespace Almirante.Api.Entities;

/// <summary>
/// Campos comuns a membro–classe e membro–especialidade (tabelas separadas). "Reconhecimento" é a investidura da classe ou a entrega
/// do distintivo da especialidade; cada tabela usa o nome próprio na coluna. Registros históricos (anteriores à implantação) não têm
/// revisão de requisitos nem validações individuais e podem ter datas desconhecidas.
/// </summary>
public abstract class VinculoFormacao
{
    public Guid Id { get; set; }
    public Guid MembroId { get; set; }
    public Guid? RevisaoId { get; set; }
    public DateOnly? DataInicio { get; set; }
    public SituacaoVinculo Situacao { get; set; } = SituacaoVinculo.EmAndamento;
    public Guid? InstrutorId { get; set; }
    public bool Historico { get; set; }
    public string? OrigemHistorica { get; set; }
    public DateOnly? DataConclusao { get; set; }
    public Guid? ConclusaoRegistradaPorUsuarioId { get; set; }
    public DateTime? ConclusaoRegistradaEmUtc { get; set; }
    public DateOnly? DataReconhecimento { get; set; }
    public Guid? ReconhecimentoRegistradoPorUsuarioId { get; set; }
    public DateTime? ReconhecimentoRegistradoEmUtc { get; set; }
    public string? Observacoes { get; set; }
    public string? MotivoCancelamento { get; set; }
    public Guid? CanceladoPorUsuarioId { get; set; }
    public DateTime? CanceladoEmUtc { get; set; }
    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }
    public byte[] Versao { get; set; } = [];

    public bool Reconhecido => ReconhecimentoRegistradoPorUsuarioId.HasValue;
}

public class MembroClasse : VinculoFormacao
{
    public ClasseDesbravador Classe { get; set; }
    public ModalidadeCumprimento Modalidade { get; set; } = ModalidadeCumprimento.Padrao;
}

public class MembroEspecialidade : VinculoFormacao
{
    public Guid EspecialidadeId { get; set; }
}

/// <summary>Progresso de um requisito de um vínculo. Sem linha, o requisito está pendente.</summary>
public class ProgressoRequisito
{
    public Guid Id { get; set; }
    public Guid? MembroClasseId { get; set; }
    public Guid? MembroEspecialidadeId { get; set; }
    public Guid RequisitoId { get; set; }
    public SituacaoRequisito Situacao { get; set; } = SituacaoRequisito.Pendente;
    public DateOnly? DataCumprimento { get; set; }
    public Guid? ValidadoPorUsuarioId { get; set; }
    public DateTime? ValidadoEmUtc { get; set; }
    public string? Observacao { get; set; }
    public string? ReferenciaEvidencia { get; set; }
    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public DateTime? AtualizadoEmUtc { get; set; }
}

/// <summary>Especialidade concluída usada como evidência de um requisito de classe. Removida logicamente.</summary>
public class EvidenciaEspecialidade
{
    public Guid Id { get; set; }
    public Guid MembroClasseId { get; set; }
    public Guid RequisitoId { get; set; }
    public Guid MembroEspecialidadeId { get; set; }
    public bool Ativo { get; set; } = true;
    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; }
    public Guid? RemovidaPorUsuarioId { get; set; }
    public DateTime? RemovidaEmUtc { get; set; }
}

/// <summary>Validação externa exigida pela referência oficial (liderança): recomendação, SGC, aprovação do campo etc.</summary>
public class ValidacaoExterna
{
    public Guid Id { get; set; }
    public Guid MembroClasseId { get; set; }
    public required string Codigo { get; set; }
    public bool Confirmada { get; set; }
    public DateOnly? Data { get; set; }
    public string? Referencia { get; set; }
    public string? Observacao { get; set; }
    public Guid RegistradaPorUsuarioId { get; set; }
    public DateTime RegistradaEmUtc { get; set; }
}

/// <summary>
/// Inclusão ou alteração nas tabelas de formação (estado anterior, null na inclusão, e estado novo), gravada só por trigger: a API não
/// escreve aqui.
/// </summary>
public class HistoricoFormacao
{
    public Guid Id { get; set; }
    public required string Tabela { get; set; }
    public Guid RegistroId { get; set; }
    public Guid? VinculoId { get; set; }
    public string? Acao { get; set; }
    public string? Motivo { get; set; }
    public string? EstadoAnteriorJson { get; set; }
    public required string EstadoNovoJson { get; set; }
    public Guid UsuarioResponsavelId { get; set; }
    public required string IpResponsavel { get; set; }
    public DateTime OcorridoEmUtc { get; set; }
}
