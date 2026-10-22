using Almirante.Api.Dtos;
using Almirante.Api.Entities;

namespace Almirante.Api.Services;

internal static class FormacaoMapping
{
    public static ClasseResumoDto ClasseResumo(ClasseDesbravador classe)
    {
        var definicao = CatalogoClasses.Obter(classe);
        return new ClasseResumoDto((int)classe, classe, definicao.Nome, definicao.Grupo);
    }

    public static EspecialidadeResumoDto EspecialidadeResumo(Especialidade e) =>
        new(e.Id, e.Nome, e.CodigoOficial, e.Area, e.Modalidade, e.Ativo);

    public static RevisaoResumoDto RevisaoResumo(RevisaoRequisitos r) =>
        new(r.Id, r.Edicao, r.FonteOficial, r.Situacao, r.CriadoEmUtc, r.PublicadaEmUtc);

    public static ClasseDto Classe(DefinicaoClasse d, RevisaoRequisitos? vigente) => new()
    {
        Id = (int)d.Classe,
        Codigo = d.Classe,
        Nome = d.Nome,
        Grupo = d.Grupo,
        Ordem = d.Ordem,
        ClasseRegularCorrespondente = d.ClasseRegularCorrespondente is { } regular ? ClasseResumo(regular) : null,
        ClasseAvancadaCorrespondente = d.ClasseAvancadaCorrespondente is { } avancada ? ClasseResumo(avancada) : null,
        IdadeReferencia = d.IdadeReferencia,
        IdadeMinimaInicio = d.IdadeMinimaInicio,
        IdadeMinimaInvestidura = d.IdadeMinimaInvestidura,
        PreRequisitosConclusao = CatalogoClasses.PreRequisitosDeConclusao(d.Classe).Select(ClasseResumo).ToList(),
        RegrasOficiais = d.RegrasOficiais,
        RequisitosExternos = d.RequisitosExternos.Select(r => new RequisitoExternoDto(r.Codigo, r.Descricao)).ToList(),
        PermiteModalidadeAgrupada = d.PermiteModalidadeAgrupada,
        ExigeAprovacaoExterna = d.ExigeAprovacaoExterna,
        RevisaoVigente = vigente is null ? null : RevisaoResumo(vigente),
    };

    public static ExigenciaEspecialidadeDto? Exigencia(Requisito r, IReadOnlyDictionary<Guid, Especialidade> especialidades) =>
        r.EspecialidadesExigidas is not { } quantidade
            ? null
            : new ExigenciaEspecialidadeDto(quantidade,
                r.EspecialidadeExigidaId is { } id && especialidades.TryGetValue(id, out var e) ? EspecialidadeResumo(e) : null,
                r.AreaEspecialidadeExigida, r.SomenteEspecialidadeNaoRealizada);

    public static IReadOnlyList<RequisitoDto> Requisitos(IReadOnlyCollection<Requisito> requisitos, IReadOnlyDictionary<Guid, Especialidade> especialidades)
    {
        var filhos = requisitos.Where(r => r.RequisitoPaiId.HasValue).ToLookup(r => r.RequisitoPaiId!.Value);

        RequisitoDto Mapear(Requisito r) => new()
        {
            Id = r.Id,
            Codigo = r.Codigo,
            Secao = r.Secao,
            Ordem = r.Ordem,
            Descricao = r.Descricao,
            Obrigatorio = r.Obrigatorio,
            Regra = r.Regra,
            QuantidadeMinima = r.QuantidadeMinima,
            ReferenciaOficial = r.ReferenciaOficial,
            ExigenciaEspecialidade = Exigencia(r, especialidades),
            Subitens = filhos[r.Id].OrderBy(f => f.Ordem).Select(Mapear).ToList(),
        };

        return requisitos.Where(r => r.RequisitoPaiId is null).OrderBy(r => r.Ordem).Select(Mapear).ToList();
    }

    public static PaginaDto<T> Pagina<T>(IReadOnlyList<T> itens, int total, int page, int pageSize) => new()
    {
        Items = itens,
        Total = total,
        Page = page,
        PageSize = pageSize,
        TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
    };
}
