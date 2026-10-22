using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using MediatR;

namespace Almirante.Api.Dtos;

public sealed record PessoaResumoDto(Guid Id, string Nome);

public sealed record ClasseResumoDto(int Id, ClasseDesbravador Codigo, string Nome, GrupoClasse Grupo);

public sealed record EspecialidadeResumoDto(Guid Id, string Nome, string? CodigoOficial, string Area, ModalidadeEspecialidade? Modalidade, bool Ativo);

public sealed record RevisaoResumoDto(Guid Id, string Edicao, string FonteOficial, SituacaoRevisao Situacao, DateTime CriadaEmUtc, DateTime? PublicadaEmUtc);

public sealed record RequisitoExternoDto(string Codigo, string Descricao);

public sealed record ReferenciaOficialDto(string Titulo, string Url, string Data);

/// <summary>Página de uma consulta administrativa (mesmo formato de GET /api/Lancamentos).</summary>
public sealed record PaginaDto<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }
}

/// <summary>Classe do catálogo fixo. id é o valor estável do enum; codigo é o nome do enum (aceito nas rotas e nos corpos).</summary>
public sealed record ClasseDto
{
    public int Id { get; init; }
    public ClasseDesbravador Codigo { get; init; }
    public required string Nome { get; init; }
    public GrupoClasse Grupo { get; init; }
    public int Ordem { get; init; }
    public ClasseResumoDto? ClasseRegularCorrespondente { get; init; }
    public ClasseResumoDto? ClasseAvancadaCorrespondente { get; init; }

    /// <summary>Idade indicada para a classe (regulares e avançadas). Não é limite máximo nem atribui a classe automaticamente.</summary>
    public int? IdadeReferencia { get; init; }
    public int? IdadeMinimaInicio { get; init; }
    public int? IdadeMinimaInvestidura { get; init; }

    /// <summary>Classes que precisam estar concluídas para concluir esta (vazio quando a referência não estabelece).</summary>
    public required IReadOnlyList<ClasseResumoDto> PreRequisitosConclusao { get; init; }
    public required IReadOnlyList<string> RegrasOficiais { get; init; }
    public required IReadOnlyList<RequisitoExternoDto> RequisitosExternos { get; init; }
    public bool PermiteModalidadeAgrupada { get; init; }
    public bool ExigeAprovacaoExterna { get; init; }

    /// <summary>Revisão publicada usada nas novas associações; null enquanto nenhuma foi publicada.</summary>
    public RevisaoResumoDto? RevisaoVigente { get; init; }
}

public sealed record ClassesResponse(IReadOnlyList<ClasseDto> Items, IReadOnlyList<ReferenciaOficialDto> Referencias);

public sealed record ListarClassesQuery : IRequest<ClassesResponse>;

public sealed record ObterClasseQuery(ClasseDesbravador Classe) : IRequest<ClasseDto?>;

/// <summary>Revisões de uma classe ou especialidade. Rascunhos e descartadas só para quem gerencia a formação.</summary>
public sealed record ListarRevisoesQuery(ClasseDesbravador? Classe, Guid? EspecialidadeId, bool IncluirNaoPublicadas)
    : IRequest<IReadOnlyList<RevisaoResumoDto>?>;

public sealed record EspecialidadeDto
{
    public Guid Id { get; init; }
    public required string Nome { get; init; }
    public string? CodigoOficial { get; init; }
    public required string Area { get; init; }
    public string? Descricao { get; init; }
    public int? NivelDificuldade { get; init; }
    public ModalidadeEspecialidade? Modalidade { get; init; }
    public required IReadOnlyList<EspecialidadeResumoDto> PreRequisitos { get; init; }
    public string? PreRequisitosTexto { get; init; }
    public string? ReferenciaDistintivo { get; init; }
    public required string FonteOficial { get; init; }
    public string? EdicaoRevisao { get; init; }
    public bool Ativo { get; init; }
    public string? MotivoInativacao { get; init; }
    public RevisaoResumoDto? RevisaoVigente { get; init; }
    public DateTime CriadoEmUtc { get; init; }
    public DateTime? AtualizadoEmUtc { get; init; }

    /// <summary>Token de concorrência (base64 do rowversion): devolva-o em PUT, DELETE e reativação.</summary>
    public required string Versao { get; init; }
}

/// <summary>Campos editáveis da especialidade. O PUT substitui todos: opcional omitido ou null fica vazio.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract class EspecialidadeConteudoRequest
{
    public string? Nome { get; set; }
    public string? CodigoOficial { get; set; }
    public string? Area { get; set; }
    public string? Descricao { get; set; }
    public int? NivelDificuldade { get; set; }
    public ModalidadeEspecialidade? Modalidade { get; set; }

    /// <summary>Ids de especialidades ativas exigidas antes da conclusão (sem ciclos).</summary>
    public IReadOnlyList<Guid>? PreRequisitos { get; set; }

    /// <summary>Pré-requisitos que não são especialidades (texto da fonte oficial).</summary>
    public string? PreRequisitosTexto { get; set; }

    /// <summary>URL https da imagem oficial do distintivo (opcional).</summary>
    public string? ReferenciaDistintivo { get; set; }
    public string? FonteOficial { get; set; }
    public string? EdicaoRevisao { get; set; }

    [JsonIgnore] public Guid UsuarioId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CadastrarEspecialidadeRequest : EspecialidadeConteudoRequest, IRequest<EspecialidadeDto>;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarEspecialidadeRequest : EspecialidadeConteudoRequest, IRequest<EspecialidadeDto>
{
    public string? Versao { get; set; }
    [JsonIgnore] public Guid Id { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AlterarSituacaoEspecialidadeRequest : IRequest<EspecialidadeDto>
{
    /// <summary>Obrigatório para inativar (1–255); ignorado na reativação.</summary>
    public string? Motivo { get; set; }
    public string? Versao { get; set; }
    [JsonIgnore] public bool Ativar { get; set; }
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

public sealed record ListarEspecialidadesQuery(string? Busca, string? Area, ModalidadeEspecialidade? Modalidade, bool IncluirInativas,
    int Page, int PageSize) : IRequest<PaginaDto<EspecialidadeDto>>;

public sealed record ObterEspecialidadeQuery(Guid Id) : IRequest<EspecialidadeDto?>;

public sealed record ExigenciaEspecialidadeDto(int Quantidade, EspecialidadeResumoDto? Especialidade, string? Area, bool NaoRealizadaAnteriormente);

public sealed record RequisitoDto
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
    public required IReadOnlyList<RequisitoDto> Subitens { get; init; }
}

public sealed record RevisaoRequisitosDto
{
    public Guid Id { get; init; }
    public ClasseResumoDto? Classe { get; init; }
    public EspecialidadeResumoDto? Especialidade { get; init; }
    public required string Edicao { get; init; }
    public required string FonteOficial { get; init; }
    public string? Observacoes { get; init; }
    public SituacaoRevisao Situacao { get; init; }
    public string? MotivoDescarte { get; init; }
    public DateTime CriadaEmUtc { get; init; }
    public DateTime? PublicadaEmUtc { get; init; }
    public int UnidadesObrigatorias { get; init; }
    public required IReadOnlyList<RequisitoDto> Requisitos { get; init; }
    public required string Versao { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ExigenciaEspecialidadeRequest
{
    /// <summary>Quantidade de especialidades concluídas exigidas (1–50).</summary>
    public int? Quantidade { get; set; }

    /// <summary>Especialidade específica exigida (opcional).</summary>
    public Guid? EspecialidadeId { get; set; }

    /// <summary>Área exigida (opcional, comparada sem diferenciar maiúsculas).</summary>
    public string? Area { get; set; }

    /// <summary>Exige especialidade concluída a partir do início da classe e que não seja evidência de outra classe.</summary>
    public bool? NaoRealizadaAnteriormente { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RequisitoRequest
{
    /// <summary>Código/número do cartão (ex.: "1", "3b"), único na revisão.</summary>
    public string? Codigo { get; set; }
    public string? Secao { get; set; }
    public string? Descricao { get; set; }

    /// <summary>Padrão true na raiz e em grupos Todos; em grupo Alternativas deve ser omitido ou false.</summary>
    public bool? Obrigatorio { get; set; }

    /// <summary>Obrigatória quando há subitens e proibida sem eles.</summary>
    public RegraGrupo? Regra { get; set; }

    /// <summary>Só com regra Alternativas: de 1 até a quantidade de subitens.</summary>
    public int? QuantidadeMinima { get; set; }
    public string? ReferenciaOficial { get; set; }

    /// <summary>Só em itens sem subitens: exige especialidades concluídas vinculadas como evidência.</summary>
    public ExigenciaEspecialidadeRequest? ExigenciaEspecialidade { get; set; }
    public IReadOnlyList<RequisitoRequest>? Subitens { get; set; }
}

/// <summary>Cria uma revisão em rascunho com todos os requisitos. A ordem é a da lista.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CriarRevisaoRequest : IRequest<RevisaoRequisitosDto>
{
    /// <summary>Informe classe OU especialidadeId.</summary>
    public ClasseDesbravador? Classe { get; set; }
    public Guid? EspecialidadeId { get; set; }

    /// <summary>Edição/revisão da fonte (ex.: "2020", "DSA 2023").</summary>
    public string? Edicao { get; set; }
    public string? FonteOficial { get; set; }

    /// <summary>Orientações posteriores aplicáveis e divergências conhecidas entre materiais.</summary>
    public string? Observacoes { get; set; }
    public IReadOnlyList<RequisitoRequest>? Requisitos { get; set; }

    [JsonIgnore] public Guid UsuarioId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AlterarSituacaoRevisaoRequest : IRequest<RevisaoRequisitosDto>
{
    /// <summary>Obrigatório para descartar (1–255).</summary>
    public string? Motivo { get; set; }
    public string? Versao { get; set; }
    [JsonIgnore] public bool Publicar { get; set; }
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

public sealed record ObterRevisaoQuery(Guid Id, bool IncluirNaoPublicadas) : IRequest<RevisaoRequisitosDto?>;
