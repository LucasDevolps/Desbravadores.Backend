using Almirante.Api.Entities;

namespace Almirante.Api.Services;

/// <summary>Especialidade vinculada como evidência de um requisito de classe, com o que é preciso para conferir a exigência.</summary>
public sealed record EvidenciaAvaliacao(
    Guid MembroEspecialidadeId,
    Guid EspecialidadeId,
    string EspecialidadeNome,
    string Area,
    SituacaoVinculo Situacao,
    DateOnly? DataConclusao,
    bool UsadaEmOutraClasse);

public sealed record EvidenciaAvaliada(EvidenciaAvaliacao Evidencia, string? MotivoInvalida)
{
    public bool Valida => MotivoInvalida is null;
}

public sealed record ResultadoRequisito(bool Satisfeito, bool ExigenciaEspecialidadesAtendida, IReadOnlyList<EvidenciaAvaliada> Evidencias);

public sealed record ResultadoProgresso(
    IReadOnlyDictionary<Guid, ResultadoRequisito> PorRequisito,
    int UnidadesObrigatorias,
    int UnidadesSatisfeitas,
    int Percentual,
    IReadOnlyList<string> RequisitosCumpridos,
    IReadOnlyList<string> RequisitosPendentes)
{
    public bool Completo => UnidadesSatisfeitas == UnidadesObrigatorias;
}

/// <summary>
/// Cálculo do progresso de um vínculo a partir da revisão de requisitos (regra documentada em docs/formacao.md):
/// <list type="bullet">
/// <item>Item (sem subitens): satisfeito quando validado e, se exigir especialidades, com evidências válidas em quantidade suficiente.</item>
/// <item>Grupo "Todos": satisfeito quando todos os subitens obrigatórios estão satisfeitos (opcionais não contam).</item>
/// <item>Grupo "Alternativas": satisfeito quando ao menos quantidadeMinima subitens estão satisfeitos.</item>
/// <item>Unidade obrigatória = requisito de nível raiz marcado como obrigatório. Percentual = satisfeitas × 100 / obrigatórias, truncado.</item>
/// </list>
/// Caixas marcadas não somam percentual por si: só contam unidades da raiz cujas regras foram atendidas.
/// </summary>
public static class ProgressoFormacao
{
    public const string Criterio =
        "Unidades = requisitos obrigatórios do nível raiz da revisão. Item: validado (e, se exigir especialidades, com evidências válidas suficientes). " +
        "Grupo Todos: todos os subitens obrigatórios satisfeitos. Grupo Alternativas: ao menos quantidadeMinima subitens satisfeitos. " +
        "Percentual = unidades satisfeitas × 100 / unidades obrigatórias, truncado. O percentual não substitui a validação final.";

    public static ResultadoProgresso Calcular(
        IReadOnlyCollection<Requisito> requisitos,
        IReadOnlyDictionary<Guid, ProgressoRequisito> progresso,
        IReadOnlyDictionary<Guid, IReadOnlyList<EvidenciaAvaliacao>> evidencias,
        DateOnly? dataInicioVinculo)
    {
        var filhos = requisitos.Where(r => r.RequisitoPaiId.HasValue).ToLookup(r => r.RequisitoPaiId!.Value);
        var resultados = new Dictionary<Guid, ResultadoRequisito>();

        bool Avaliar(Requisito requisito)
        {
            var subitens = filhos[requisito.Id].OrderBy(r => r.Ordem).ToList();
            var satisfeitos = subitens.Select(Avaliar).ToList();

            var avaliadas = (evidencias.TryGetValue(requisito.Id, out var lista) ? lista : [])
                .Select(e => new EvidenciaAvaliada(e, MotivoEvidenciaInvalida(requisito, e, dataInicioVinculo)))
                .ToList();
            var exigenciaAtendida = requisito.EspecialidadesExigidas is not { } exigidas || avaliadas.Count(e => e.Valida) >= exigidas;

            bool satisfeito = requisito.Regra switch
            {
                RegraGrupo.Todos => subitens.Zip(satisfeitos).Where(p => p.First.Obrigatorio).All(p => p.Second),
                RegraGrupo.Alternativas => satisfeitos.Count(s => s) >= (requisito.QuantidadeMinima ?? 1),
                _ => progresso.TryGetValue(requisito.Id, out var p) && p.Situacao == SituacaoRequisito.Validado && exigenciaAtendida,
            };

            resultados[requisito.Id] = new ResultadoRequisito(satisfeito, exigenciaAtendida, avaliadas);
            return satisfeito;
        }

        var raizes = requisitos.Where(r => r.RequisitoPaiId is null).OrderBy(r => r.Ordem).ToList();
        var cumpridos = new List<string>();
        var pendentes = new List<string>();
        foreach (var raiz in raizes)
        {
            var satisfeito = Avaliar(raiz);
            if (raiz.Obrigatorio)
            {
                (satisfeito ? cumpridos : pendentes).Add(raiz.Codigo);
            }
        }

        var obrigatorias = cumpridos.Count + pendentes.Count;
        var percentual = obrigatorias == 0 ? 0 : cumpridos.Count * 100 / obrigatorias;
        return new ResultadoProgresso(resultados, obrigatorias, cumpridos.Count, percentual, cumpridos, pendentes);
    }

    /// <summary>Por que uma especialidade vinculada não serve de evidência para o requisito; null quando serve.</summary>
    public static string? MotivoEvidenciaInvalida(Requisito requisito, EvidenciaAvaliacao evidencia, DateOnly? dataInicioVinculo)
    {
        if (requisito.EspecialidadesExigidas is null)
        {
            return "O requisito não exige especialidades.";
        }

        if (evidencia.Situacao != SituacaoVinculo.Concluida)
        {
            return "A especialidade não está concluída.";
        }

        if (requisito.EspecialidadeExigidaId is { } exigida && exigida != evidencia.EspecialidadeId)
        {
            return "O requisito exige outra especialidade.";
        }

        if (requisito.AreaEspecialidadeExigida is { } area && !string.Equals(area.Trim(), evidencia.Area.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return $"O requisito exige especialidade da área {area}.";
        }

        if (requisito.SomenteEspecialidadeNaoRealizada)
        {
            if (evidencia.DataConclusao is not { } conclusao || dataInicioVinculo is not { } inicio || conclusao < inicio)
            {
                return "O requisito exige especialidade não realizada anteriormente: a conclusão precisa ser conhecida e a partir do início da classe.";
            }

            if (evidencia.UsadaEmOutraClasse)
            {
                return "O requisito exige especialidade não realizada anteriormente, e esta já é evidência de outra classe.";
            }
        }

        return null;
    }
}
