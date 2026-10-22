using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using Almirante.Api.Validation.Formacao;

namespace Almirante.Api.Tests;

/// <summary>Issue #125: catálogo fixo de classes, cálculo de progresso (grupos, alternativas, evidências) e validação das entradas.</summary>
public sealed class FormacaoUnitTests
{
    private static readonly Guid Revisao = Guid.NewGuid();

    private static Requisito Item(string codigo, Guid? pai = null, bool obrigatorio = true, int ordem = 1, RegraGrupo? regra = null,
        int? minimo = null, int? especialidades = null, string? area = null, Guid? especialidadeId = null, bool naoRealizada = false) => new()
    {
        Id = Guid.NewGuid(), RevisaoId = Revisao, RequisitoPaiId = pai, Codigo = codigo, Descricao = codigo, Ordem = ordem, Obrigatorio = obrigatorio,
        Regra = regra, QuantidadeMinima = minimo, EspecialidadesExigidas = especialidades, AreaEspecialidadeExigida = area,
        EspecialidadeExigidaId = especialidadeId, SomenteEspecialidadeNaoRealizada = naoRealizada,
    };

    private static Dictionary<Guid, ProgressoRequisito> Validados(params Requisito[] requisitos) =>
        requisitos.ToDictionary(r => r.Id, r => new ProgressoRequisito { RequisitoId = r.Id, Situacao = SituacaoRequisito.Validado });

    private static ResultadoProgresso Calcular(IReadOnlyCollection<Requisito> requisitos, Dictionary<Guid, ProgressoRequisito> progresso,
        Dictionary<Guid, IReadOnlyList<EvidenciaAvaliacao>>? evidencias = null, DateOnly? inicio = null) =>
        ProgressoFormacao.Calcular(requisitos, progresso, evidencias ?? [], inicio ?? new DateOnly(2026, 1, 1));

    private static EvidenciaAvaliacao Conquista(string area = "Estudos da Natureza", SituacaoVinculo situacao = SituacaoVinculo.Concluida,
        DateOnly? conclusao = null, bool usadaEmOutra = false, Guid? especialidadeId = null) =>
        new(Guid.NewGuid(), especialidadeId ?? Guid.NewGuid(), "Especialidade", area, situacao, conclusao ?? new DateOnly(2026, 3, 1), usadaEmOutra);

    [Fact]
    public void Catalogo_Tem15ClassesComIdsEstaveis_GruposOrdemECorrespondencias()
    {
        var esperado = new Dictionary<ClasseDesbravador, int>
        {
            [ClasseDesbravador.Amigo] = 1, [ClasseDesbravador.Companheiro] = 2, [ClasseDesbravador.Pesquisador] = 3, [ClasseDesbravador.Pioneiro] = 4,
            [ClasseDesbravador.Excursionista] = 5, [ClasseDesbravador.Guia] = 6, [ClasseDesbravador.AmigoDaNatureza] = 11,
            [ClasseDesbravador.CompanheiroDeExcursionismo] = 12, [ClasseDesbravador.PesquisadorDeCampoEBosque] = 13,
            [ClasseDesbravador.PioneiroDeNovasFronteiras] = 14, [ClasseDesbravador.ExcursionistaNaMata] = 15, [ClasseDesbravador.GuiaDeExploracao] = 16,
            [ClasseDesbravador.Lider] = 21, [ClasseDesbravador.LiderMaster] = 22, [ClasseDesbravador.LiderMasterAvancado] = 23,
        };
        Assert.Equal(esperado.OrderBy(e => e.Value), Enum.GetValues<ClasseDesbravador>().Select(c => KeyValuePair.Create(c, (int)c)));

        var todas = CatalogoClasses.Todas;
        Assert.Equal(15, todas.Count);
        Assert.Equal(Enumerable.Range(1, 15), todas.Select(d => d.Ordem));
        Assert.Equal((6, 6, 3), (todas.Count(d => d.Grupo == GrupoClasse.Regular), todas.Count(d => d.Grupo == GrupoClasse.Avancada),
            todas.Count(d => d.Grupo == GrupoClasse.Lideranca)));
        Assert.Equal(["Líder", "Líder Máster", "Líder Máster Avançado"], todas.Where(d => d.Grupo == GrupoClasse.Lideranca).Select(d => d.Nome));

        foreach (var avancada in todas.Where(d => d.Grupo == GrupoClasse.Avancada))
        {
            var regular = CatalogoClasses.Obter(avancada.ClasseRegularCorrespondente!.Value);
            Assert.Equal((GrupoClasse.Regular, avancada.Classe, avancada.IdadeReferencia),
                (regular.Grupo, regular.ClasseAvancadaCorrespondente!.Value, regular.IdadeReferencia));
            Assert.Empty(CatalogoClasses.PreRequisitosDeConclusao(avancada.Classe));
        }

        Assert.Equal([10, 11, 12, 13, 14, 15], todas.Where(d => d.Grupo == GrupoClasse.Regular).Select(d => d.IdadeReferencia!.Value));
        Assert.All(todas.Where(d => d.Grupo != GrupoClasse.Lideranca), d => Assert.True(d.PermiteModalidadeAgrupada && !d.ExigeAprovacaoExterna));
        Assert.All(todas.Where(d => d.Grupo == GrupoClasse.Lideranca), d => Assert.True(!d.PermiteModalidadeAgrupada && d.ExigeAprovacaoExterna
            && d.RequisitosExternos.Any(e => e.Codigo == CatalogoClasses.AprovacaoOficial)));
    }

    [Fact]
    public void Catalogo_RegrasDeLideranca_SaoProgressivasComIdadesDaReferencia()
    {
        Assert.Equal(CatalogoClasses.Regulares, CatalogoClasses.PreRequisitosDeConclusao(ClasseDesbravador.Lider));
        Assert.Equal([ClasseDesbravador.Lider], CatalogoClasses.PreRequisitosDeConclusao(ClasseDesbravador.LiderMaster));
        Assert.Equal([ClasseDesbravador.LiderMaster], CatalogoClasses.PreRequisitosDeConclusao(ClasseDesbravador.LiderMasterAvancado));
        var lider = CatalogoClasses.Obter(ClasseDesbravador.Lider);
        Assert.Equal((16, 18, (int?)null), (lider.IdadeMinimaInicio, lider.IdadeMinimaInvestidura, lider.IdadeReferencia));
        Assert.Equal(18, CatalogoClasses.Obter(ClasseDesbravador.LiderMaster).IdadeMinimaInicio);
        Assert.All(CatalogoClasses.Todas.Where(d => d.Grupo != GrupoClasse.Lideranca), d => Assert.Null(d.IdadeMinimaInicio));
    }

    [Fact]
    public void Progresso_GrupoTodos_ExigeSoOsObrigatorios_EContaUnidadesDaRaiz()
    {
        var grupo = Item("1", regra: RegraGrupo.Todos);
        var a = Item("1a", grupo.Id, ordem: 1);
        var b = Item("1b", grupo.Id, ordem: 2);
        var opcional = Item("1c", grupo.Id, obrigatorio: false, ordem: 3);
        var simples = Item("2", ordem: 2);
        var extra = Item("3", obrigatorio: false, ordem: 3);
        Requisito[] todos = [grupo, a, b, opcional, simples, extra];

        var parcial = Calcular(todos, Validados(a, simples, extra));
        Assert.Equal((2, 1, 50, false), (parcial.UnidadesObrigatorias, parcial.UnidadesSatisfeitas, parcial.Percentual, parcial.Completo));
        Assert.Equal(["2"], parcial.RequisitosCumpridos);
        Assert.Equal(["1"], parcial.RequisitosPendentes);

        var completo = Calcular(todos, Validados(a, b, simples));
        Assert.True(completo.Completo);
        Assert.True(completo.PorRequisito[grupo.Id].Satisfeito);
        Assert.False(completo.PorRequisito[opcional.Id].Satisfeito);
        Assert.Equal(100, completo.Percentual);
    }

    [Fact]
    public void Progresso_Alternativas_EscolherUmaENEntreOpcoes_NaoContaCaixasMarcadas()
    {
        var uma = Item("1", regra: RegraGrupo.Alternativas, minimo: 1);
        var u1 = Item("1a", uma.Id, obrigatorio: false, ordem: 1);
        var u2 = Item("1b", uma.Id, obrigatorio: false, ordem: 2);
        var duas = Item("2", regra: RegraGrupo.Alternativas, minimo: 2, ordem: 2);
        var d1 = Item("2a", duas.Id, obrigatorio: false, ordem: 1);
        var d2 = Item("2b", duas.Id, obrigatorio: false, ordem: 2);
        var d3 = Item("2c", duas.Id, obrigatorio: false, ordem: 3);
        Requisito[] todos = [uma, u1, u2, duas, d1, d2, d3];

        // Três caixas marcadas, mas o grupo "2 entre 3" só tem uma: não satisfaz e o percentual não sobe por quantidade de caixas.
        var r = Calcular(todos, Validados(u1, u2, d1));
        Assert.Equal((true, false, 50), (r.PorRequisito[uma.Id].Satisfeito, r.PorRequisito[duas.Id].Satisfeito, r.Percentual));

        var emAndamento = Validados(u2, d1);
        emAndamento[d3.Id] = new ProgressoRequisito { RequisitoId = d3.Id, Situacao = SituacaoRequisito.EmAndamento };
        Assert.False(Calcular(todos, emAndamento).PorRequisito[duas.Id].Satisfeito);
        Assert.True(Calcular(todos, Validados(u2, d1, d3)).Completo);
    }

    [Fact]
    public void Progresso_GruposAninhados_PropagamASatisfacao()
    {
        var raiz = Item("1", regra: RegraGrupo.Todos);
        var alternativas = Item("1a", raiz.Id, regra: RegraGrupo.Alternativas, minimo: 1);
        var opcaoComSubitens = Item("1a-i", alternativas.Id, obrigatorio: false, regra: RegraGrupo.Todos);
        var x = Item("1a-i-x", opcaoComSubitens.Id);
        var y = Item("1a-i-y", opcaoComSubitens.Id, ordem: 2);
        var outra = Item("1a-ii", alternativas.Id, obrigatorio: false, ordem: 2);
        Requisito[] todos = [raiz, alternativas, opcaoComSubitens, x, y, outra];

        Assert.False(Calcular(todos, Validados(x)).Completo);
        Assert.True(Calcular(todos, Validados(x, y)).Completo);
        Assert.True(Calcular(todos, Validados(outra)).Completo);
    }

    [Fact]
    public void Progresso_ItemComEspecialidades_ExigeValidacaoEEvidenciasValidas_SemDuplaContagem()
    {
        var item = Item("5", especialidades: 2, area: "Estudos da Natureza");
        var validado = Validados(item);

        var umaSo = Calcular([item], validado, new() { [item.Id] = [Conquista()] });
        Assert.False(umaSo.Completo);
        Assert.False(umaSo.PorRequisito[item.Id].ExigenciaEspecialidadesAtendida);

        var outraArea = Calcular([item], validado, new() { [item.Id] = [Conquista(), Conquista("ADRA")] });
        Assert.False(outraArea.Completo);
        Assert.Contains("área", outraArea.PorRequisito[item.Id].Evidencias.Single(e => !e.Valida).MotivoInvalida);

        var naoConcluida = Calcular([item], validado, new() { [item.Id] = [Conquista(), Conquista(situacao: SituacaoVinculo.EmAndamento)] });
        Assert.False(naoConcluida.Completo);

        var duas = new Dictionary<Guid, IReadOnlyList<EvidenciaAvaliacao>> { [item.Id] = [Conquista(), Conquista()] };
        Assert.True(Calcular([item], validado, duas).Completo);
        Assert.False(Calcular([item], [], duas).Completo);
    }

    [Fact]
    public void Evidencia_NaoRealizadaAnteriormente_ExigeConclusaoAposInicio_ENaoUsadaEmOutraClasse()
    {
        var especifica = Guid.NewGuid();
        var item = Item("7", especialidades: 1, especialidadeId: especifica, naoRealizada: true);
        var inicio = new DateOnly(2026, 2, 1);

        Assert.Null(ProgressoFormacao.MotivoEvidenciaInvalida(item, Conquista(especialidadeId: especifica, conclusao: inicio), inicio));
        Assert.Contains("não realizada", ProgressoFormacao.MotivoEvidenciaInvalida(item,
            Conquista(especialidadeId: especifica, conclusao: inicio.AddDays(-1)), inicio));
        Assert.Contains("outra classe", ProgressoFormacao.MotivoEvidenciaInvalida(item,
            Conquista(especialidadeId: especifica, usadaEmOutra: true), inicio));
        Assert.Contains("outra especialidade", ProgressoFormacao.MotivoEvidenciaInvalida(item, Conquista(), inicio));
        Assert.NotNull(ProgressoFormacao.MotivoEvidenciaInvalida(item, Conquista(especialidadeId: especifica) with { DataConclusao = null }, inicio));
        Assert.Contains("não exige", ProgressoFormacao.MotivoEvidenciaInvalida(Item("8"), Conquista(), inicio));
    }

    private static CriarRevisaoRequest Revisao_(params RequisitoRequest[] requisitos) => new()
    {
        Classe = ClasseDesbravador.Amigo, Edicao = "2020", FonteOficial = "Cartão oficial DSA", Requisitos = requisitos,
    };

    private static RequisitoRequest R(string codigo, RegraGrupo? regra = null, int? minimo = null, bool? obrigatorio = null,
        ExigenciaEspecialidadeRequest? exigencia = null, params RequisitoRequest[] subitens) => new()
    {
        Codigo = codigo, Descricao = $"Requisito {codigo}", Regra = regra, QuantidadeMinima = minimo, Obrigatorio = obrigatorio,
        ExigenciaEspecialidade = exigencia, Subitens = subitens.Length == 0 ? null : subitens,
    };

    private static List<string> Erros(CriarRevisaoRequest request) =>
        new CriarRevisaoRequestValidator().Validate(request).Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}").ToList();

    [Fact]
    public void Revisao_ArvoreValida_EhAceita()
    {
        Assert.Empty(Erros(Revisao_(
            R("1"),
            R("2", RegraGrupo.Alternativas, 2, null, null, R("2a"), R("2b", obrigatorio: false), R("2c")),
            R("3", RegraGrupo.Todos, null, null, null, R("3a"), R("3b", obrigatorio: false)),
            R("4", exigencia: new ExigenciaEspecialidadeRequest { Quantidade = 1, Area = "ADRA", NaoRealizadaAnteriormente = true }),
            R("5", obrigatorio: false))));
    }

    [Fact]
    public void Revisao_RegrasDeGrupo_CodigosEExigencias_SaoValidadas()
    {
        var erros = Erros(Revisao_(
            R("1", RegraGrupo.Alternativas, 3, null, null, R("1a"), R("1b")),
            R("2", null, null, null, null, R("2a")),
            R("3", RegraGrupo.Todos, 1, null, null, R("3a")),
            R("4", RegraGrupo.Alternativas, 1, null, null, R("4a", obrigatorio: true)),
            R("5", RegraGrupo.Todos, null, null, null, R("5a", obrigatorio: false)),
            R("1"),
            R("6", RegraGrupo.Todos, null, null, new ExigenciaEspecialidadeRequest { Quantidade = 1 }, R("6a")),
            R("7", exigencia: new ExigenciaEspecialidadeRequest { Quantidade = 0 }),
            R("8", RegraGrupo.Todos)));

        Assert.Contains(erros, e => e.StartsWith("requisitos[0].quantidadeMinima"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[1].regra"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[2].quantidadeMinima"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[3].subitens[0].obrigatorio"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[4].subitens"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[5].codigo") && e.Contains("repetido"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[6].exigenciaEspecialidade"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[7].exigenciaEspecialidade.quantidade"));
        Assert.Contains(erros, e => e.StartsWith("requisitos[8].regra"));
    }

    [Fact]
    public void Revisao_AlvoUnico_RaizComObrigatorio_ProfundidadeETexto()
    {
        Assert.Contains(Erros(new CriarRevisaoRequest { Edicao = "1", FonteOficial = "f", Requisitos = [R("1")] }), e => e.Contains("classe OU especialidadeId"));
        Assert.Contains(Erros(Revisao_(R("1", obrigatorio: false))), e => e.Contains("ao menos um requisito obrigatório"));
        Assert.Contains(Erros(Revisao_()), e => e.StartsWith("requisitos"));
        var comEmoji = R("1");
        comEmoji.Descricao = "Emoji 🙂";
        Assert.Contains(Erros(Revisao_(comEmoji)), e => e.StartsWith("requisitos[0].descricao"));

        var profundo = R("5");
        foreach (var codigo in new[] { "4", "3", "2", "1" })
        {
            profundo = R(codigo, RegraGrupo.Todos, null, null, null, profundo);
        }

        Assert.Contains(Erros(Revisao_(profundo)), e => e.Contains("níveis"));
        var classeInexistente = Revisao_(R("1"));
        classeInexistente.Classe = (ClasseDesbravador)99;
        Assert.Contains(Erros(classeInexistente), e => e.StartsWith("Classe"));
    }

    [Fact]
    public void Especialidade_ValidaTextoNivelUrlEPreRequisitos()
    {
        var validator = new CadastrarEspecialidadeRequestValidator();
        Assert.True(validator.Validate(new CadastrarEspecialidadeRequest
        {
            Nome = "Natação Principiante I", Area = "Atividades Recreativas", FonteOficial = "Manual de especialidades DSA",
            NivelDificuldade = 1, Modalidade = ModalidadeEspecialidade.Basica, ReferenciaDistintivo = "https://exemplo.org/natacao.png",
        }).IsValid);

        var erros = validator.Validate(new CadastrarEspecialidadeRequest
        {
            Nome = " ", Area = new string('a', 101), FonteOficial = "ok", NivelDificuldade = 4, Modalidade = (ModalidadeEspecialidade)9,
            ReferenciaDistintivo = "http://inseguro.org/x.png", PreRequisitos = [Guid.Empty, Guid.Empty], CodigoOficial = "AR-001☃",
        }).Errors.Select(e => e.PropertyName).ToList();
        Assert.Equal(["Area", "CodigoOficial", "Modalidade", "NivelDificuldade", "Nome", "PreRequisitos", "ReferenciaDistintivo"], erros.Order());
    }

    [Fact]
    public void Historico_ExigeOrigem_DatasEmOrdem_ECamposDoTipo()
    {
        var validator = new RegistrarHistoricoRequestValidator();
        var valido = new RegistrarHistoricoRequest
        {
            Tipo = TipoVinculo.Classe, MembroId = Guid.NewGuid(), Classe = ClasseDesbravador.Amigo, OrigemHistorica = "Cartão arquivado",
            InvestiduraOuEntregaRealizada = true, DataConclusao = new DateOnly(2020, 5, 1), DataInvestiduraOuEntrega = null,
        };
        Assert.True(validator.Validate(valido).IsValid);

        var erros = validator.Validate(new RegistrarHistoricoRequest
        {
            Tipo = TipoVinculo.Especialidade, MembroId = Guid.NewGuid(), Classe = ClasseDesbravador.Amigo, InvestiduraOuEntregaRealizada = false,
            DataInicio = new DateOnly(2020, 5, 1), DataConclusao = new DateOnly(2020, 4, 1), DataInvestiduraOuEntrega = new DateOnly(2020, 6, 1),
        }).Errors.Select(e => e.PropertyName).Distinct().Order().ToList();
        Assert.Equal(["Classe", "DataConclusao", "DataInvestiduraOuEntrega", "EspecialidadeId", "OrigemHistorica"], erros);
    }

    [Fact]
    public void ListagemDeVinculos_FiltrosDoTipo_PeriodoEPaginacao()
    {
        var validator = new ListarVinculosQueryValidator();
        Assert.True(validator.Validate(new ListarVinculosQuery
        {
            Tipo = TipoVinculo.Classe, Grupo = GrupoClasse.Lideranca, Periodo = PeriodoFormacao.Conclusao, De = new DateOnly(2026, 1, 1),
        }).IsValid);

        string[] Falhas(ListarVinculosQuery q) => validator.Validate(q).Errors.Select(e => e.PropertyName).Order().ToArray();
        Assert.Equal(["especialidadeId"], Falhas(new ListarVinculosQuery { Tipo = TipoVinculo.Classe, Area = "ADRA" }));
        Assert.Equal(["classe"], Falhas(new ListarVinculosQuery { Tipo = TipoVinculo.Especialidade, Grupo = GrupoClasse.Regular }));
        Assert.Equal(["periodo"], Falhas(new ListarVinculosQuery { Tipo = TipoVinculo.Classe, De = new DateOnly(2026, 1, 1) }));
        Assert.Equal(["periodo"], Falhas(new ListarVinculosQuery { Tipo = TipoVinculo.Classe, Periodo = PeriodoFormacao.Inicio }));
        Assert.Equal(["de"], Falhas(new ListarVinculosQuery
        {
            Tipo = TipoVinculo.Classe, Periodo = PeriodoFormacao.Inicio, De = new DateOnly(2026, 2, 1), Ate = new DateOnly(2026, 1, 1),
        }));
        Assert.Equal(["page", "pageSize"], Falhas(new ListarVinculosQuery { Tipo = TipoVinculo.Classe, Page = 0, PageSize = 101 }));
    }
}
