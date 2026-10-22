using System.Text.Json;
using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using MediatR;

namespace Almirante.Api.Dtos;

/// <summary>Registro de conclusão, investidura (classe) ou entrega (especialidade). data é null só em registro histórico sem data conhecida.</summary>
public sealed record RegistroFormacaoDto(DateOnly? Data, PessoaResumoDto RegistradoPor, DateTime RegistradoEmUtc);

public sealed record CancelamentoDto(string Motivo, PessoaResumoDto CanceladoPor, DateTime CanceladoEmUtc);

public sealed record ProgressoResumoDto
{
    public int UnidadesObrigatorias { get; init; }
    public int UnidadesSatisfeitas { get; init; }
    public int Percentual { get; init; }

    /// <summary>Códigos das unidades obrigatórias (raiz) satisfeitas e pendentes.</summary>
    public required IReadOnlyList<string> RequisitosCumpridos { get; init; }
    public required IReadOnlyList<string> RequisitosPendentes { get; init; }
}

public sealed record PreRequisitoAvaliadoDto(string Descricao, bool Atendido, string? Detalhe);

public sealed record ProgressoDto
{
    public required string Criterio { get; init; }
    public int UnidadesObrigatorias { get; init; }
    public int UnidadesSatisfeitas { get; init; }
    public int Percentual { get; init; }
    public required IReadOnlyList<string> RequisitosCumpridos { get; init; }
    public required IReadOnlyList<string> RequisitosPendentes { get; init; }
    public bool RequisitosCompletos { get; init; }
    public required IReadOnlyList<PreRequisitoAvaliadoDto> PreRequisitos { get; init; }

    /// <summary>Se a conclusão pode ser registrada agora pelo fluxo normal; bloqueios explica o que falta.</summary>
    public bool PodeConcluir { get; init; }
    public required IReadOnlyList<string> Bloqueios { get; init; }
}

public sealed record EvidenciaDto(Guid MembroEspecialidadeId, Guid EspecialidadeId, string EspecialidadeNome, SituacaoVinculo Situacao,
    DateOnly? DataConclusao, bool Valida, string? MotivoInvalida);

public sealed record RequisitoProgressoDto
{
    public Guid Id { get; init; }
    public required string Codigo { get; init; }
    public string? Secao { get; init; }
    public int Ordem { get; init; }
    public required string Descricao { get; init; }
    public bool Obrigatorio { get; init; }
    public RegraGrupo? Regra { get; init; }
    public int? QuantidadeMinima { get; init; }
    public string? ReferenciaOficial { get; init; }
    public ExigenciaEspecialidadeDto? ExigenciaEspecialidade { get; init; }

    /// <summary>Resultado das regras (item validado com evidências suficientes; grupo conforme a regra).</summary>
    public bool Satisfeito { get; init; }

    /// <summary>Só em itens (null em grupos, que não recebem progresso direto).</summary>
    public SituacaoRequisito? Situacao { get; init; }
    public DateOnly? DataCumprimento { get; init; }
    public PessoaResumoDto? ValidadoPor { get; init; }
    public DateTime? ValidadoEmUtc { get; init; }
    public string? Observacao { get; init; }
    public string? ReferenciaEvidencia { get; init; }
    public required IReadOnlyList<EvidenciaDto> Evidencias { get; init; }
    public required IReadOnlyList<RequisitoProgressoDto> Subitens { get; init; }
}

public sealed record ValidacaoExternaDto(string Codigo, string Descricao, bool Confirmada, DateOnly? Data, string? Referencia,
    string? Observacao, PessoaResumoDto? RegistradaPor, DateTime? RegistradaEmUtc);

/// <summary>Item das listas e relatórios. classe/modalidade só em vínculos de classe; especialidade só em vínculos de especialidade.</summary>
public record VinculoResumoDto
{
    public Guid Id { get; init; }
    public TipoVinculo Tipo { get; init; }
    public required PessoaResumoDto Membro { get; init; }
    public ClasseResumoDto? Classe { get; init; }
    public EspecialidadeResumoDto? Especialidade { get; init; }
    public ModalidadeCumprimento? Modalidade { get; init; }
    public SituacaoVinculo Situacao { get; init; }
    public DateOnly? DataInicio { get; init; }
    public DateOnly? DataConclusao { get; init; }

    /// <summary>Conquista anterior à implantação, sem validações individuais de requisitos.</summary>
    public bool Historico { get; init; }

    /// <summary>Investidura da classe (null em especialidades).</summary>
    public RegistroFormacaoDto? Investidura { get; init; }

    /// <summary>Entrega do distintivo da especialidade (null em classes).</summary>
    public RegistroFormacaoDto? Entrega { get; init; }

    /// <summary>Concluída sem investidura/entrega registrada.</summary>
    public bool AguardandoInvestiduraOuEntrega { get; init; }

    /// <summary>null em registro histórico (não há requisitos acompanhados).</summary>
    public ProgressoResumoDto? Progresso { get; init; }
    public required string Versao { get; init; }
}

public sealed record VinculoDto : VinculoResumoDto
{
    public RevisaoResumoDto? Revisao { get; init; }
    public PessoaResumoDto? Instrutor { get; init; }
    public string? OrigemHistorica { get; init; }
    public RegistroFormacaoDto? Conclusao { get; init; }
    public CancelamentoDto? Cancelamento { get; init; }
    public string? Observacoes { get; init; }

    /// <summary>Validações externas exigidas pela referência oficial (classes de liderança); vazio nas demais.</summary>
    public required IReadOnlyList<ValidacaoExternaDto> ValidacoesExternas { get; init; }

    /// <summary>
    /// Liderança: true só com a aprovação oficial registrada. A conclusão local não equivale a aprovação oficial. null fora da liderança.
    /// </summary>
    public bool? AprovacaoOficialConfirmada { get; init; }
    public ProgressoDto? ProgressoDetalhado { get; init; }
    public required IReadOnlyList<RequisitoProgressoDto> Requisitos { get; init; }
    public required PessoaResumoDto CriadoPor { get; init; }
    public DateTime CriadoEmUtc { get; init; }
    public DateTime? AtualizadoEmUtc { get; init; }
}

/// <summary>Uma inclusão (estadoAnterior null) ou alteração; os estados trazem todas as colunas da linha, com os nomes do banco.</summary>
public sealed record HistoricoFormacaoDto(Guid Id, string Tabela, Guid RegistroId, string? Acao, string? Motivo,
    PessoaResumoDto UsuarioResponsavel, DateTime OcorridoEmUtc, JsonElement? EstadoAnterior, JsonElement EstadoNovo);

public sealed record FormacaoMembroDto(PessoaResumoDto Membro, bool MembroAtivo, IReadOnlyList<VinculoResumoDto> Classes,
    IReadOnlyList<VinculoResumoDto> Especialidades);

public sealed record ParticipantesPorSituacaoDto
{
    public TipoVinculo Tipo { get; init; }
    public ClasseResumoDto? Classe { get; init; }
    public EspecialidadeResumoDto? Especialidade { get; init; }
    public int EmAndamento { get; init; }
    public int Concluidas { get; init; }
    public int ConcluidasAguardandoInvestiduraOuEntrega { get; init; }
    public int Canceladas { get; init; }
    public int Total { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<OrdenacaoVinculos>))]
public enum OrdenacaoVinculos
{
    Membro = 1,
    Inicio = 2,
    Conclusao = 3,
    InvestiduraOuEntrega = 4,
}

/// <summary>Filtros das consultas administrativas. periodo define qual data de/ate filtram (início, conclusão ou investidura/entrega).</summary>
public sealed record ListarVinculosQuery : IRequest<PaginaDto<VinculoResumoDto>>
{
    public TipoVinculo Tipo { get; init; }
    public Guid? MembroId { get; init; }
    public ClasseDesbravador? Classe { get; init; }
    public GrupoClasse? Grupo { get; init; }
    public Guid? EspecialidadeId { get; init; }
    public string? Area { get; init; }
    public SituacaoVinculo? Situacao { get; init; }
    public bool? Historico { get; init; }
    public bool? AguardandoInvestiduraOuEntrega { get; init; }
    public PeriodoFormacao? Periodo { get; init; }
    public DateOnly? De { get; init; }
    public DateOnly? Ate { get; init; }
    public OrdenacaoVinculos OrdenarPor { get; init; } = OrdenacaoVinculos.Membro;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = Services.RegrasFormacao.PageSizePadrao;
}

/// <summary>Detalhe de um vínculo. Com SomenteDoMembroId (autoatendimento), outro membro recebe 404 como se não existisse.</summary>
public sealed record ObterVinculoQuery(TipoVinculo Tipo, Guid Id, Guid? SomenteDoMembroId = null) : IRequest<VinculoDto?>;

public sealed record ListarHistoricoVinculoQuery(TipoVinculo Tipo, Guid Id) : IRequest<IReadOnlyList<HistoricoFormacaoDto>?>;

public sealed record ObterFormacaoMembroQuery(Guid MembroId) : IRequest<FormacaoMembroDto?>;

public sealed record RelatorioParticipantesQuery(TipoVinculo Tipo, ClasseDesbravador? Classe, GrupoClasse? Grupo, Guid? EspecialidadeId, string? Area)
    : IRequest<IReadOnlyList<ParticipantesPorSituacaoDto>>;

/// <summary>Identificação do vínculo, do responsável e da origem, preenchida pelo controller (nunca pelo corpo).</summary>
public abstract class ComandoVinculoRequest : IRequest<VinculoDto>
{
    [JsonIgnore] public TipoVinculo Tipo { get; set; }
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AssociarClasseRequest : ComandoVinculoRequest
{
    public Guid? MembroId { get; set; }
    public ClasseDesbravador? Classe { get; set; }

    /// <summary>Padrao (omitido) ou Agrupada (só regulares e avançadas).</summary>
    public ModalidadeCumprimento? Modalidade { get; set; }
    public DateOnly? DataInicio { get; set; }
    public Guid? InstrutorId { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AssociarEspecialidadeRequest : ComandoVinculoRequest
{
    public Guid? MembroId { get; set; }
    public Guid? EspecialidadeId { get; set; }
    public DateOnly? DataInicio { get; set; }
    public Guid? InstrutorId { get; set; }
    public string? Observacoes { get; set; }
}

/// <summary>Conquista anterior à implantação: sempre concluída, sem requisitos validados; datas desconhecidas ficam null.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegistrarHistoricoRequest : ComandoVinculoRequest
{
    public Guid? MembroId { get; set; }

    /// <summary>Vínculo de classe: obrigatório. Vínculo de especialidade: proibido.</summary>
    public ClasseDesbravador? Classe { get; set; }
    public ModalidadeCumprimento? Modalidade { get; set; }

    /// <summary>Vínculo de especialidade: obrigatório. Vínculo de classe: proibido.</summary>
    public Guid? EspecialidadeId { get; set; }
    public DateOnly? DataInicio { get; set; }
    public DateOnly? DataConclusao { get; set; }

    /// <summary>Investidura (classe) ou entrega (especialidade) já ocorrida.</summary>
    public bool? InvestiduraOuEntregaRealizada { get; set; }
    public DateOnly? DataInvestiduraOuEntrega { get; set; }

    /// <summary>Origem da informação (ex.: "cartão físico arquivado na secretaria", "SGC"). Obrigatória.</summary>
    public string? OrigemHistorica { get; set; }
    public string? Observacoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarVinculoRequest : ComandoVinculoRequest
{
    /// <summary>Substituído (null remove). A condição de instrutor não concede permissão de escrita.</summary>
    public Guid? InstrutorId { get; set; }
    public string? Observacoes { get; set; }
    public string? Versao { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarRequisitoVinculoRequest : ComandoVinculoRequest
{
    public SituacaoRequisito? Situacao { get; set; }

    /// <summary>Data em que o requisito foi cumprido (opcional; entre o início do vínculo e hoje).</summary>
    public DateOnly? DataCumprimento { get; set; }
    public string? Observacao { get; set; }
    public string? ReferenciaEvidencia { get; set; }

    /// <summary>Obrigatória para tirar um requisito de Validado.</summary>
    public string? Justificativa { get; set; }

    [JsonIgnore] public Guid RequisitoId { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VincularEvidenciaRequest : ComandoVinculoRequest
{
    public Guid? MembroEspecialidadeId { get; set; }
    [JsonIgnore] public Guid RequisitoId { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RemoverEvidenciaRequest : ComandoVinculoRequest
{
    public string? Justificativa { get; set; }
    [JsonIgnore] public Guid RequisitoId { get; set; }
    [JsonIgnore] public Guid MembroEspecialidadeId { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegistrarValidacaoExternaRequest : ComandoVinculoRequest
{
    public bool? Confirmada { get; set; }

    /// <summary>Obrigatória ao confirmar; não futura.</summary>
    public DateOnly? Data { get; set; }

    /// <summary>Documento, protocolo ou ato que comprova (ex.: ata da comissão, número no SGC).</summary>
    public string? Referencia { get; set; }
    public string? Observacao { get; set; }

    /// <summary>Obrigatória para desfazer uma confirmação.</summary>
    public string? Justificativa { get; set; }

    [JsonIgnore] public string Codigo { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ConcluirVinculoRequest : ComandoVinculoRequest
{
    public DateOnly? DataConclusao { get; set; }
    public string? Versao { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegistrarReconhecimentoRequest : ComandoVinculoRequest
{
    /// <summary>Data da investidura (classe) ou da entrega do distintivo (especialidade).</summary>
    public DateOnly? Data { get; set; }
    public string? Versao { get; set; }
}

public enum OperacaoVinculo
{
    Cancelar = 1,
    Retomar = 2,
    Reabrir = 3,
    DesfazerReconhecimento = 4,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class OperacaoVinculoRequest : ComandoVinculoRequest
{
    /// <summary>Obrigatória (1–255): fica no histórico com o estado anterior.</summary>
    public string? Justificativa { get; set; }
    public string? Versao { get; set; }
    [JsonIgnore] public OperacaoVinculo Operacao { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MigrarRevisaoRequest : ComandoVinculoRequest
{
    public Guid? RevisaoId { get; set; }
    public string? Justificativa { get; set; }
    public string? Versao { get; set; }
}
