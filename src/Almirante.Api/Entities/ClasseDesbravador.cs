using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

/// <summary>
/// Catálogo fixo das classes (issue #125). Os valores numéricos são estáveis e gravados no banco: nunca renumere nem reaproveite um valor.
/// Requisitos não fazem parte do enum; ficam em revisões persistidas (<see cref="RevisaoRequisitos"/>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClasseDesbravador>))]
public enum ClasseDesbravador
{
    Amigo = 1,
    Companheiro = 2,
    Pesquisador = 3,
    Pioneiro = 4,
    Excursionista = 5,
    Guia = 6,

    AmigoDaNatureza = 11,
    CompanheiroDeExcursionismo = 12,
    PesquisadorDeCampoEBosque = 13,
    PioneiroDeNovasFronteiras = 14,
    ExcursionistaNaMata = 15,
    GuiaDeExploracao = 16,

    Lider = 21,
    LiderMaster = 22,
    LiderMasterAvancado = 23,
}

[JsonConverter(typeof(JsonStringEnumConverter<GrupoClasse>))]
public enum GrupoClasse
{
    Regular = 1,
    Avancada = 2,
    Lideranca = 3,
}

/// <summary>Como a classe é cumprida. Classes agrupadas são uma modalidade das classes existentes, nunca uma classe própria.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModalidadeCumprimento>))]
public enum ModalidadeCumprimento
{
    Padrao = 1,
    Agrupada = 2,
}

/// <summary>Modalidade básica/avançada da especialidade, independente do nível de dificuldade.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModalidadeEspecialidade>))]
public enum ModalidadeEspecialidade
{
    Basica = 1,
    Avancada = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<SituacaoVinculo>))]
public enum SituacaoVinculo
{
    EmAndamento = 1,
    Concluida = 2,
    Cancelada = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<SituacaoRequisito>))]
public enum SituacaoRequisito
{
    Pendente = 1,
    EmAndamento = 2,
    Validado = 3,
}

/// <summary>Rascunho é a única situação editável (descartar ou publicar); publicada é a vigente; substituída continua valendo para quem já a usa.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SituacaoRevisao>))]
public enum SituacaoRevisao
{
    Rascunho = 1,
    Publicada = 2,
    Substituida = 3,
    Descartada = 4,
}

/// <summary>Regra de satisfação de um requisito com subitens.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegraGrupo>))]
public enum RegraGrupo
{
    /// <summary>Todos os subitens obrigatórios.</summary>
    Todos = 1,

    /// <summary>Ao menos quantidadeMinima subitens (escolher uma opção: quantidadeMinima = 1).</summary>
    Alternativas = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<TipoVinculo>))]
public enum TipoVinculo
{
    Classe = 1,
    Especialidade = 2,
}

/// <summary>Qual data o período dos filtros administrativos usa.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeriodoFormacao>))]
public enum PeriodoFormacao
{
    Inicio = 1,
    Conclusao = 2,

    /// <summary>Investidura (classes) ou entrega do distintivo (especialidades).</summary>
    InvestiduraOuEntrega = 3,
}
