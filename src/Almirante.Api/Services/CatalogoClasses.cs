using Almirante.Api.Entities;

namespace Almirante.Api.Services;

/// <summary>Validação que depende de órgão ou documento externo ao clube; o acompanhamento local só registra que foi conferida.</summary>
public sealed record RequisitoExterno(string Codigo, string Descricao);

public sealed record ReferenciaOficial(string Titulo, string Url, string Data);

/// <summary>
/// Dados fixos de uma classe. Idade de referência não é limite máximo nem atribui a classe automaticamente; idades mínimas e
/// pré-requisitos só existem quando a referência oficial adotada (<see cref="CatalogoClasses.Referencias"/>) os estabelece.
/// </summary>
public sealed record DefinicaoClasse(
    ClasseDesbravador Classe,
    string Nome,
    GrupoClasse Grupo,
    int Ordem,
    ClasseDesbravador? ClasseRegularCorrespondente,
    ClasseDesbravador? ClasseAvancadaCorrespondente,
    int? IdadeReferencia,
    int? IdadeMinimaInicio,
    int? IdadeMinimaInvestidura,
    IReadOnlyList<string> RegrasOficiais,
    IReadOnlyList<RequisitoExterno> RequisitosExternos)
{
    public bool PermiteModalidadeAgrupada => Grupo != GrupoClasse.Lideranca;
    public bool ExigeAprovacaoExterna => RequisitosExternos.Count > 0;
}

/// <summary>
/// Catálogo das 15 classes (regulares, avançadas e de liderança). Não há CRUD: o enum é a identidade estável e este catálogo traz os
/// dados das páginas oficiais da DSA (<see cref="Referencias"/>). As regras de liderança aplicadas pela API estão em
/// <see cref="FormacaoService"/> e documentadas em docs/formacao.md.
/// </summary>
public static class CatalogoClasses
{
    public const string AprovacaoOficial = "APROVACAO_OFICIAL";
    public const string RecomendacaoComissaoIgreja = "RECOMENDACAO_COMISSAO_IGREJA";
    public const string CadastroSgc = "CADASTRO_SGC";
    public const string MembroBatizado = "MEMBRO_BATIZADO";
    public const string MembroAtivoIgreja = "MEMBRO_ATIVO_IGREJA";
    public const string ExperienciaLiderInvestido = "EXPERIENCIA_LIDER_INVESTIDO";

    /// <summary>Prazo máximo para cumprir a classe de Líder Máster, contado do início.</summary>
    public const int PrazoLiderMasterAnos = 3;

    /// <summary>Tempo mínimo como líder investido para iniciar Líder Máster.</summary>
    public const int ExperienciaLiderMasterAnos = 1;

    public static readonly IReadOnlyList<ReferenciaOficial> Referencias =
    [
        new("Classes de Desbravadores (DSA)", "https://www.adventistas.org/pt/desbravadores/classes/", "2017-11-06"),
        new("Requisitos das classes de liderança (DSA)", "https://www.adventistas.org/pt/desbravadores/classes/classes-de-lideranca-requisitos/", "2017-11-06"),
        new("Manual Administrativo dos Desbravadores, edição 2020, seções 4.1 e 4.2",
            "https://deptos.adventistas.org/desbravadores/ManualAdministrativoDesbravadores.pdf", "2020"),
    ];

    private static readonly RequisitoExterno Aprovacao =
        new(AprovacaoOficial, "Aprovação/autorização oficial para a investidura pelo campo (Associação/Missão, União ou Divisão, conforme a classe).");

    private static readonly RequisitoExterno Recomendacao =
        new(RecomendacaoComissaoIgreja, "Recomendação por escrito da comissão da igreja para iniciar a classe.");

    private static readonly RequisitoExterno Sgc =
        new(CadastroSgc, "Membro ativo de um clube (ou de coordenação distrital/regional) com o cadastro atualizado no SGC.");

    private static readonly IReadOnlyList<DefinicaoClasse> Definicoes =
    [
        Regular(ClasseDesbravador.Amigo, "Amigo", 1, ClasseDesbravador.AmigoDaNatureza, 10),
        Regular(ClasseDesbravador.Companheiro, "Companheiro", 2, ClasseDesbravador.CompanheiroDeExcursionismo, 11),
        Regular(ClasseDesbravador.Pesquisador, "Pesquisador", 3, ClasseDesbravador.PesquisadorDeCampoEBosque, 12),
        Regular(ClasseDesbravador.Pioneiro, "Pioneiro", 4, ClasseDesbravador.PioneiroDeNovasFronteiras, 13),
        Regular(ClasseDesbravador.Excursionista, "Excursionista", 5, ClasseDesbravador.ExcursionistaNaMata, 14),
        Regular(ClasseDesbravador.Guia, "Guia", 6, ClasseDesbravador.GuiaDeExploracao, 15),

        Avancada(ClasseDesbravador.AmigoDaNatureza, "Amigo da Natureza", 7, ClasseDesbravador.Amigo, "Amigo", 10),
        Avancada(ClasseDesbravador.CompanheiroDeExcursionismo, "Companheiro de Excursionismo", 8, ClasseDesbravador.Companheiro, "Companheiro", 11),
        Avancada(ClasseDesbravador.PesquisadorDeCampoEBosque, "Pesquisador de Campo e Bosque", 9, ClasseDesbravador.Pesquisador, "Pesquisador", 12),
        Avancada(ClasseDesbravador.PioneiroDeNovasFronteiras, "Pioneiro de Novas Fronteiras", 10, ClasseDesbravador.Pioneiro, "Pioneiro", 13),
        Avancada(ClasseDesbravador.ExcursionistaNaMata, "Excursionista na Mata", 11, ClasseDesbravador.Excursionista, "Excursionista", 14),
        Avancada(ClasseDesbravador.GuiaDeExploracao, "Guia de Exploração", 12, ClasseDesbravador.Guia, "Guia", 15),

        new(ClasseDesbravador.Lider, "Líder", GrupoClasse.Lideranca, 13, null, null, null, 16, 18,
        [
            "Ter, no mínimo, 16 anos completos para iniciar a classe e, no mínimo, 18 anos completos para a investidura.",
            "Ter concluído todas as classes regulares, ou estar simultaneamente cumprindo os requisitos de classes agrupadas e líder.",
            "Ser membro batizado da Igreja Adventista do Sétimo Dia.",
            "Possuir recomendação por escrito da comissão da igreja para iniciar a classe.",
            "Ser membro ativo de um clube ou de coordenação distrital/regional, com o cadastro atualizado no SGC.",
        ],
        [new(MembroBatizado, "Membro batizado da Igreja Adventista do Sétimo Dia."), Recomendacao, Sgc, Aprovacao]),

        new(ClasseDesbravador.LiderMaster, "Líder Máster", GrupoClasse.Lideranca, 14, null, null, null, 18, null,
        [
            "Ter, no mínimo, um ano de experiência como líder investido para começar a cumprir os requisitos.",
            "Ter 18 anos completos para iniciar esta classe.",
            "Ser membro ativo da Igreja Adventista do Sétimo Dia.",
            "Possuir recomendação por escrito da comissão da igreja para iniciar a classe.",
            "Ser membro ativo de um clube ou de coordenação distrital/regional, com o cadastro atualizado no SGC.",
            "Cumprir a classe em no máximo três anos; trabalhos de classes anteriores não são reaproveitados.",
        ],
        [new(MembroAtivoIgreja, "Membro ativo da Igreja Adventista do Sétimo Dia."), Recomendacao, Sgc, Aprovacao]),

        new(ClasseDesbravador.LiderMasterAvancado, "Líder Máster Avançado", GrupoClasse.Lideranca, 15, null, null, null, null, null,
        [
            "As classes de liderança são progressivas: Líder Máster é pré-requisito do Líder Máster Avançado.",
            "O portfólio inclui a recomendação por escrito no formulário MDA/DSA-LMA.",
            "A investidura é realizada pelo Ministério de Desbravadores da Divisão ou União, com autorização.",
        ],
        [new(RecomendacaoComissaoIgreja, "Recomendação por escrito da comissão da igreja (formulário MDA/DSA-LMA)."), Aprovacao]),
    ];

    private static readonly IReadOnlyDictionary<ClasseDesbravador, DefinicaoClasse> PorClasse = Definicoes.ToDictionary(d => d.Classe);

    public static IReadOnlyList<DefinicaoClasse> Todas => Definicoes;

    public static IReadOnlyList<ClasseDesbravador> Regulares { get; } =
        Definicoes.Where(d => d.Grupo == GrupoClasse.Regular).Select(d => d.Classe).ToList();

    public static DefinicaoClasse Obter(ClasseDesbravador classe) => PorClasse[classe];

    public static bool Existe(ClasseDesbravador classe) => PorClasse.ContainsKey(classe);

    public static string Nome(ClasseDesbravador classe) => PorClasse[classe].Nome;

    /// <summary>Classe que precisa estar concluída para concluir <paramref name="classe"/>; vazio quando a referência não estabelece.</summary>
    public static IReadOnlyList<ClasseDesbravador> PreRequisitosDeConclusao(ClasseDesbravador classe) => classe switch
    {
        ClasseDesbravador.Lider => Regulares,
        ClasseDesbravador.LiderMaster => [ClasseDesbravador.Lider],
        ClasseDesbravador.LiderMasterAvancado => [ClasseDesbravador.LiderMaster],
        _ => [],
    };

    private static DefinicaoClasse Regular(ClasseDesbravador classe, string nome, int ordem, ClasseDesbravador avancada, int idade) =>
        new(classe, nome, GrupoClasse.Regular, ordem, null, avancada, idade, null, null,
            [$"Idade de referência: {idade} anos (indicação da faixa etária, não limite máximo).",
             "Pode ser cumprida na modalidade de classes agrupadas, por quem ingressa após os 10 anos."], []);

    private static DefinicaoClasse Avancada(ClasseDesbravador classe, string nome, int ordem, ClasseDesbravador regular, string nomeRegular, int idade) =>
        new(classe, nome, GrupoClasse.Avancada, ordem, regular, null, idade, null, null,
            [$"Corresponde à classe regular {nomeRegular} (mesma idade de referência: {idade} anos).",
             "A referência adotada não estabelece pré-requisito entre a classe regular e a avançada correspondente.",
             "No sistema de classes agrupadas, as avançadas ficam no fim do cartão, por coluna de idade."], []);
}
