using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>Consultas, relatórios e regras de pré-requisito do acompanhamento de formação.</summary>
public sealed partial class FormacaoService
{
    private sealed record ContextoMembro(Usuario Membro, IReadOnlyList<MembroClasse> Classes, IReadOnlyList<MembroEspecialidade> Especialidades);

    private sealed record EvidenciaCarregada(Guid MembroClasseId, Guid RequisitoId, EvidenciaAvaliacao Avaliacao);

    private sealed record AvaliacaoVinculo(
        IReadOnlyList<Requisito> Requisitos,
        IReadOnlyDictionary<Guid, ProgressoRequisito> Progresso,
        ResultadoProgresso? Resultado,
        IReadOnlyList<PreRequisitoAvaliadoDto> PreRequisitos,
        IReadOnlyList<string> Bloqueios,
        DateOnly? UltimoCumprimento);

    private sealed class DadosProgresso(
        ILookup<Guid, Requisito> requisitosPorRevisao,
        ILookup<Guid, ProgressoRequisito> progressoPorVinculo,
        ILookup<Guid, EvidenciaCarregada> evidenciasPorVinculo)
    {
        public IReadOnlyList<Requisito> Requisitos(VinculoFormacao v) => v.RevisaoId is { } id ? requisitosPorRevisao[id].ToList() : [];

        public Dictionary<Guid, ProgressoRequisito> Progresso(VinculoFormacao v) => progressoPorVinculo[v.Id].ToDictionary(p => p.RequisitoId);

        public ResultadoProgresso? Calcular(VinculoFormacao v) => v.RevisaoId is null
            ? null
            : ProgressoFormacao.Calcular(Requisitos(v), Progresso(v),
                evidenciasPorVinculo[v.Id].GroupBy(e => e.RequisitoId)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<EvidenciaAvaliacao>)g.Select(e => e.Avaliacao).ToList()),
                v.DataInicio);
    }

    public async Task<VinculoDto?> ObterAsync(ObterVinculoQuery query, CancellationToken ct)
    {
        VinculoFormacao? vinculo = query.Tipo == TipoVinculo.Classe
            ? await db.MembrosClasses.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.Id, ct)
            : await db.MembrosEspecialidades.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.Id, ct);
        if (vinculo is null || (query.SomenteDoMembroId is { } membroId && vinculo.MembroId != membroId))
        {
            return null;
        }

        var avaliacao = await AvaliarVinculoAsync(query.Tipo, vinculo, dataCivil.Hoje(), ct);
        var revisao = vinculo.RevisaoId is { } revisaoId
            ? await db.RevisoesRequisitos.AsNoTracking().SingleAsync(r => r.Id == revisaoId, ct)
            : null;
        var validacoes = vinculo is MembroClasse
            ? await db.ValidacoesExternas.AsNoTracking().Where(v => v.MembroClasseId == vinculo.Id).ToListAsync(ct)
            : [];

        var especialidadeIds = avaliacao.Requisitos.Where(r => r.EspecialidadeExigidaId.HasValue).Select(r => r.EspecialidadeExigidaId!.Value).ToList();
        if (vinculo is MembroEspecialidade me)
        {
            especialidadeIds.Add(me.EspecialidadeId);
        }

        var especialidades = await db.Especialidades.AsNoTracking().Where(e => especialidadeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var pessoas = await PessoasAsync(
            [vinculo.MembroId, vinculo.CriadoPorUsuarioId, vinculo.InstrutorId, vinculo.ConclusaoRegistradaPorUsuarioId,
             vinculo.ReconhecimentoRegistradoPorUsuarioId, vinculo.CanceladoPorUsuarioId,
             .. avaliacao.Progresso.Values.Select(p => p.ValidadoPorUsuarioId), .. validacoes.Select(v => (Guid?)v.RegistradaPorUsuarioId)], ct);

        var resumo = Resumir(vinculo, pessoas, especialidades, avaliacao.Resultado);
        var filhos = avaliacao.Requisitos.Where(r => r.RequisitoPaiId.HasValue).ToLookup(r => r.RequisitoPaiId!.Value);

        RequisitoProgressoDto Mapear(Requisito r)
        {
            avaliacao.Progresso.TryGetValue(r.Id, out var p);
            var resultado = avaliacao.Resultado!.PorRequisito[r.Id];
            return new RequisitoProgressoDto
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
                ExigenciaEspecialidade = FormacaoMapping.Exigencia(r, especialidades),
                Satisfeito = resultado.Satisfeito,
                Situacao = r.Regra is null ? p?.Situacao ?? SituacaoRequisito.Pendente : null,
                DataCumprimento = p?.DataCumprimento,
                ValidadoPor = p?.ValidadoPorUsuarioId is { } validador ? pessoas[validador] : null,
                ValidadoEmUtc = p?.ValidadoEmUtc,
                Observacao = p?.Observacao,
                ReferenciaEvidencia = p?.ReferenciaEvidencia,
                Evidencias = resultado.Evidencias.Select(e => new EvidenciaDto(e.Evidencia.MembroEspecialidadeId, e.Evidencia.EspecialidadeId,
                    e.Evidencia.EspecialidadeNome, e.Evidencia.Situacao, e.Evidencia.DataConclusao, e.Valida, e.MotivoInvalida)).ToList(),
                Subitens = filhos[r.Id].OrderBy(f => f.Ordem).Select(Mapear).ToList(),
            };
        }

        var definicao = vinculo is MembroClasse classe ? CatalogoClasses.Obter(classe.Classe) : null;
        var emAndamento = vinculo.Situacao == SituacaoVinculo.EmAndamento && !vinculo.Historico;
        return new VinculoDto
        {
            Id = resumo.Id,
            Tipo = resumo.Tipo,
            Membro = resumo.Membro,
            Classe = resumo.Classe,
            Especialidade = resumo.Especialidade,
            Modalidade = resumo.Modalidade,
            Situacao = resumo.Situacao,
            DataInicio = resumo.DataInicio,
            DataConclusao = resumo.DataConclusao,
            Historico = resumo.Historico,
            Investidura = resumo.Investidura,
            Entrega = resumo.Entrega,
            AguardandoInvestiduraOuEntrega = resumo.AguardandoInvestiduraOuEntrega,
            Progresso = resumo.Progresso,
            Versao = resumo.Versao,
            Revisao = revisao is null ? null : FormacaoMapping.RevisaoResumo(revisao),
            Instrutor = vinculo.InstrutorId is { } instrutor ? pessoas[instrutor] : null,
            OrigemHistorica = vinculo.OrigemHistorica,
            Conclusao = vinculo.ConclusaoRegistradaPorUsuarioId is { } concluiu
                ? new RegistroFormacaoDto(vinculo.DataConclusao, pessoas[concluiu], vinculo.ConclusaoRegistradaEmUtc!.Value)
                : null,
            Cancelamento = vinculo.CanceladoPorUsuarioId is { } cancelou
                ? new CancelamentoDto(vinculo.MotivoCancelamento!, pessoas[cancelou], vinculo.CanceladoEmUtc!.Value)
                : null,
            Observacoes = vinculo.Observacoes,
            ValidacoesExternas = definicao?.RequisitosExternos.Select(e =>
            {
                var v = validacoes.SingleOrDefault(x => x.Codigo == e.Codigo);
                return new ValidacaoExternaDto(e.Codigo, e.Descricao, v?.Confirmada ?? false, v?.Data, v?.Referencia, v?.Observacao,
                    v is null ? null : pessoas[v.RegistradaPorUsuarioId], v?.RegistradaEmUtc);
            }).ToList() ?? [],
            AprovacaoOficialConfirmada = definicao is { ExigeAprovacaoExterna: true }
                ? validacoes.Any(v => v.Codigo == CatalogoClasses.AprovacaoOficial && v.Confirmada)
                : null,
            ProgressoDetalhado = avaliacao.Resultado is not { } calculado ? null : new ProgressoDto
            {
                Criterio = ProgressoFormacao.Criterio,
                UnidadesObrigatorias = calculado.UnidadesObrigatorias,
                UnidadesSatisfeitas = calculado.UnidadesSatisfeitas,
                Percentual = calculado.Percentual,
                RequisitosCumpridos = calculado.RequisitosCumpridos,
                RequisitosPendentes = calculado.RequisitosPendentes,
                RequisitosCompletos = calculado.Completo,
                PreRequisitos = avaliacao.PreRequisitos,
                PodeConcluir = emAndamento && avaliacao.Bloqueios.Count == 0,
                Bloqueios = emAndamento ? avaliacao.Bloqueios : [],
            },
            Requisitos = avaliacao.Resultado is null
                ? []
                : avaliacao.Requisitos.Where(r => r.RequisitoPaiId is null).OrderBy(r => r.Ordem).Select(Mapear).ToList(),
            CriadoPor = pessoas[vinculo.CriadoPorUsuarioId],
            CriadoEmUtc = vinculo.CriadoEmUtc,
            AtualizadoEmUtc = vinculo.AtualizadoEmUtc,
        };
    }

    public async Task<PaginaDto<VinculoResumoDto>> ListarAsync(ListarVinculosQuery f, CancellationToken ct)
    {
        int total;
        List<VinculoFormacao> pagina;
        if (f.Tipo == TipoVinculo.Classe)
        {
            var consulta = Filtrar(db.MembrosClasses.AsNoTracking(), f);
            if (f.Classe is { } classe)
            {
                consulta = consulta.Where(v => v.Classe == classe);
            }

            if (f.Grupo is { } grupo)
            {
                var classes = CatalogoClasses.Todas.Where(d => d.Grupo == grupo).Select(d => d.Classe).ToList();
                consulta = consulta.Where(v => classes.Contains(v.Classe));
            }

            total = await consulta.CountAsync(ct);
            pagina = [.. await Ordenar(consulta, f.OrdenarPor).ThenBy(v => v.Classe).ThenBy(v => v.Id)
                .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync(ct)];
        }
        else
        {
            var consulta = Filtrar(db.MembrosEspecialidades.AsNoTracking(), f);
            if (f.EspecialidadeId is { } especialidadeId)
            {
                consulta = consulta.Where(v => v.EspecialidadeId == especialidadeId);
            }

            if (FormacaoBanco.Opcional(f.Area) is { } area)
            {
                consulta = consulta.Where(v => db.Especialidades.Any(e => e.Id == v.EspecialidadeId && e.Area == area));
            }

            total = await consulta.CountAsync(ct);
            pagina = [.. await Ordenar(consulta, f.OrdenarPor).ThenBy(v => v.EspecialidadeId).ThenBy(v => v.Id)
                .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync(ct)];
        }

        return FormacaoMapping.Pagina(await ResumirAsync(pagina, ct), total, f.Page, f.PageSize);
    }

    public async Task<IReadOnlyList<HistoricoFormacaoDto>?> ListarHistoricoAsync(ListarHistoricoVinculoQuery query, CancellationToken ct)
    {
        var existe = query.Tipo == TipoVinculo.Classe
            ? await db.MembrosClasses.AnyAsync(v => v.Id == query.Id, ct)
            : await db.MembrosEspecialidades.AnyAsync(v => v.Id == query.Id, ct);
        if (!existe)
        {
            return null;
        }

        var linhas = await (
            from h in db.HistoricoFormacao.AsNoTracking()
            join u in db.Usuarios.AsNoTracking() on h.UsuarioResponsavelId equals u.Id
            where h.VinculoId == query.Id
            orderby h.OcorridoEmUtc, h.Id
            select new { h, u.Nome }).ToListAsync(ct);

        return linhas.Select(l => new HistoricoFormacaoDto(l.h.Id, l.h.Tabela, l.h.RegistroId, l.h.Acao, l.h.Motivo,
            new PessoaResumoDto(l.h.UsuarioResponsavelId, l.Nome), DateTime.SpecifyKind(l.h.OcorridoEmUtc, DateTimeKind.Utc),
            l.h.EstadoAnteriorJson is null ? null : Json(l.h.EstadoAnteriorJson), Json(l.h.EstadoNovoJson))).ToList();
    }

    public async Task<FormacaoMembroDto?> ObterFormacaoMembroAsync(Guid membroId, CancellationToken ct)
    {
        var membro = await db.Usuarios.AsNoTracking().SingleOrDefaultAsync(u => u.Id == membroId, ct);
        if (membro is null)
        {
            return null;
        }

        var classes = await db.MembrosClasses.AsNoTracking().Where(v => v.MembroId == membroId).ToListAsync(ct);
        var especialidades = await db.MembrosEspecialidades.AsNoTracking().Where(v => v.MembroId == membroId).ToListAsync(ct);
        var resumos = await ResumirAsync([.. classes, .. especialidades], ct);
        return new FormacaoMembroDto(new PessoaResumoDto(membro.Id, membro.Nome), membro.Ativo,
            resumos.Where(r => r.Tipo == TipoVinculo.Classe)
                .OrderBy(r => CatalogoClasses.Obter(r.Classe!.Codigo).Ordem).ThenBy(r => r.Situacao).ThenBy(r => r.Id).ToList(),
            resumos.Where(r => r.Tipo == TipoVinculo.Especialidade)
                .OrderBy(r => r.Especialidade!.Nome).ThenBy(r => r.Situacao).ThenBy(r => r.Id).ToList());
    }

    public async Task<IReadOnlyList<ParticipantesPorSituacaoDto>> RelatorioParticipantesAsync(RelatorioParticipantesQuery query, CancellationToken ct)
    {
        if (query.Tipo == TipoVinculo.Classe)
        {
            var classes = CatalogoClasses.Todas
                .Where(d => (query.Classe is null || d.Classe == query.Classe) && (query.Grupo is null || d.Grupo == query.Grupo))
                .ToList();
            var codigos = classes.Select(d => d.Classe).ToList();
            var contagens = await db.MembrosClasses.AsNoTracking().Where(v => codigos.Contains(v.Classe)).GroupBy(v => v.Classe)
                .Select(g => new
                {
                    g.Key,
                    EmAndamento = g.Count(v => v.Situacao == SituacaoVinculo.EmAndamento),
                    Concluidas = g.Count(v => v.Situacao == SituacaoVinculo.Concluida),
                    Aguardando = g.Count(v => v.Situacao == SituacaoVinculo.Concluida && v.ReconhecimentoRegistradoPorUsuarioId == null),
                    Canceladas = g.Count(v => v.Situacao == SituacaoVinculo.Cancelada),
                    Total = g.Count(),
                }).ToDictionaryAsync(c => c.Key, ct);

            return classes.Select(d =>
            {
                var c = contagens.GetValueOrDefault(d.Classe);
                return new ParticipantesPorSituacaoDto
                {
                    Tipo = TipoVinculo.Classe,
                    Classe = FormacaoMapping.ClasseResumo(d.Classe),
                    EmAndamento = c?.EmAndamento ?? 0,
                    Concluidas = c?.Concluidas ?? 0,
                    ConcluidasAguardandoInvestiduraOuEntrega = c?.Aguardando ?? 0,
                    Canceladas = c?.Canceladas ?? 0,
                    Total = c?.Total ?? 0,
                };
            }).ToList();
        }

        var vinculos = db.MembrosEspecialidades.AsNoTracking();
        if (query.EspecialidadeId is { } especialidadeId)
        {
            vinculos = vinculos.Where(v => v.EspecialidadeId == especialidadeId);
        }

        if (FormacaoBanco.Opcional(query.Area) is { } area)
        {
            vinculos = vinculos.Where(v => db.Especialidades.Any(e => e.Id == v.EspecialidadeId && e.Area == area));
        }

        var porEspecialidade = await vinculos.GroupBy(v => v.EspecialidadeId)
            .Select(g => new
            {
                g.Key,
                EmAndamento = g.Count(v => v.Situacao == SituacaoVinculo.EmAndamento),
                Concluidas = g.Count(v => v.Situacao == SituacaoVinculo.Concluida),
                Aguardando = g.Count(v => v.Situacao == SituacaoVinculo.Concluida && v.ReconhecimentoRegistradoPorUsuarioId == null),
                Canceladas = g.Count(v => v.Situacao == SituacaoVinculo.Cancelada),
                Total = g.Count(),
            }).ToListAsync(ct);
        var ids = porEspecialidade.Select(p => p.Key).ToList();
        var especialidades = await db.Especialidades.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        return porEspecialidade.Select(p => new ParticipantesPorSituacaoDto
        {
            Tipo = TipoVinculo.Especialidade,
            Especialidade = FormacaoMapping.EspecialidadeResumo(especialidades[p.Key]),
            EmAndamento = p.EmAndamento,
            Concluidas = p.Concluidas,
            ConcluidasAguardandoInvestiduraOuEntrega = p.Aguardando,
            Canceladas = p.Canceladas,
            Total = p.Total,
        }).OrderBy(p => p.Especialidade!.Area).ThenBy(p => p.Especialidade!.Nome).ThenBy(p => p.Especialidade!.Id).ToList();
    }

    private async Task<AvaliacaoVinculo> AvaliarVinculoAsync(TipoVinculo tipo, VinculoFormacao vinculo, DateOnly dataReferencia, CancellationToken ct)
    {
        var dados = await CarregarProgressoAsync([vinculo], ct);
        var resultado = dados.Calcular(vinculo);
        var progresso = dados.Progresso(vinculo);
        var requisitos = dados.Requisitos(vinculo);
        var idsRevisao = requisitos.Select(r => r.Id).ToHashSet();
        var membro = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == vinculo.MembroId, ct);
        var contexto = await ContextoAsync(membro, ct);

        var preRequisitos = new List<PreRequisitoAvaliadoDto>();
        if (vinculo is MembroClasse classe)
        {
            preRequisitos.AddRange(CatalogoClasses.PreRequisitosDeConclusao(classe.Classe).Select(c => ConclusaoDe(contexto, c)));
            if (classe.Classe == ClasseDesbravador.LiderMaster && classe.DataInicio is { } inicio)
            {
                var limite = inicio.AddYears(CatalogoClasses.PrazoLiderMasterAnos);
                preRequisitos.Add(new PreRequisitoAvaliadoDto($"Concluir em até {CatalogoClasses.PrazoLiderMasterAnos} anos do início (até {limite:yyyy-MM-dd})",
                    dataReferencia <= limite, dataReferencia <= limite ? null : "prazo excedido"));
            }
        }
        else if (vinculo is MembroEspecialidade especialidade)
        {
            var exigidas = await (
                from p in db.EspecialidadesPreRequisitos.AsNoTracking()
                join e in db.Especialidades.AsNoTracking() on p.PreRequisitoId equals e.Id
                where p.Ativo && p.EspecialidadeId == especialidade.EspecialidadeId
                orderby e.Nome
                select e).ToListAsync(ct);
            preRequisitos.AddRange(exigidas.Select(e => new PreRequisitoAvaliadoDto($"Especialidade {e.Nome} concluída",
                contexto.Especialidades.Any(v => v.EspecialidadeId == e.Id && v.Situacao == SituacaoVinculo.Concluida), null)));
        }

        var bloqueios = new List<string>();
        if (resultado is { Completo: false })
        {
            bloqueios.Add($"Requisitos obrigatórios pendentes: {string.Join(", ", resultado.RequisitosPendentes)}.");
        }

        bloqueios.AddRange(preRequisitos.Where(p => !p.Atendido).Select(p => $"Pré-requisito não atendido: {p.Descricao}" +
            (p.Detalhe is null ? "." : $" ({p.Detalhe}).")));

        var ultimo = progresso.Values.Where(p => idsRevisao.Contains(p.RequisitoId) && p.Situacao == SituacaoRequisito.Validado)
            .Select(p => p.DataCumprimento).Max();
        return new AvaliacaoVinculo(requisitos, progresso, resultado, preRequisitos, bloqueios, ultimo);
    }

    private async Task<List<PreRequisitoAvaliadoDto>> AvaliarInvestiduraAsync(MembroClasse classe, DateOnly data, CancellationToken ct)
    {
        var definicao = CatalogoClasses.Obter(classe.Classe);
        var pendentes = new List<PreRequisitoAvaliadoDto>();
        if (definicao.IdadeMinimaInvestidura is { } idade)
        {
            var membro = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == classe.MembroId, ct);
            pendentes.Add(AvaliarIdade(membro, idade, data, "a investidura"));
        }

        var confirmadas = await db.ValidacoesExternas.AsNoTracking().Where(v => v.MembroClasseId == classe.Id && v.Confirmada)
            .Select(v => v.Codigo).ToListAsync(ct);
        pendentes.AddRange(definicao.RequisitosExternos.Where(e => !confirmadas.Contains(e.Codigo))
            .Select(e => new PreRequisitoAvaliadoDto(e.Descricao, false, $"validação externa {e.Codigo} não confirmada")));
        return pendentes.Where(p => !p.Atendido).ToList();
    }

    /// <summary>Regras oficiais de início das classes de liderança (idade mínima e classes anteriores). As demais não têm.</summary>
    private static List<PreRequisitoAvaliadoDto> AvaliarInicio(ClasseDesbravador classe, DateOnly dataInicio, ContextoMembro contexto)
    {
        var lista = new List<PreRequisitoAvaliadoDto>();
        if (CatalogoClasses.Obter(classe).IdadeMinimaInicio is { } idade)
        {
            lista.Add(AvaliarIdade(contexto.Membro, idade, dataInicio, "iniciar a classe"));
        }

        switch (classe)
        {
            case ClasseDesbravador.Lider:
                var faltantes = CatalogoClasses.Regulares.Where(c => !Concluiu(contexto, c)).ToList();
                var agrupadas = contexto.Classes.Any(c => c.Modalidade == ModalidadeCumprimento.Agrupada && c.Situacao != SituacaoVinculo.Cancelada);
                var atendido = faltantes.Count == 0 || agrupadas;
                lista.Add(new PreRequisitoAvaliadoDto("Ter concluído todas as classes regulares ou estar cumprindo classes agrupadas", atendido,
                    atendido ? null : "faltam " + string.Join(", ", faltantes.Select(CatalogoClasses.Nome))));
                break;
            case ClasseDesbravador.LiderMaster:
                var lider = contexto.Classes.FirstOrDefault(c => c.Classe == ClasseDesbravador.Lider && c.Situacao == SituacaoVinculo.Concluida);
                var minimo = lider?.DataReconhecimento?.AddYears(CatalogoClasses.ExperienciaLiderMasterAnos);
                var experiente = lider is { Reconhecido: true } && minimo <= dataInicio;
                lista.Add(new PreRequisitoAvaliadoDto($"Ter no mínimo {CatalogoClasses.ExperienciaLiderMasterAnos} ano de experiência como líder investido",
                    experiente, experiente ? null
                        : lider is null ? "classe Líder não concluída"
                        : !lider.Reconhecido ? "investidura de Líder não registrada"
                        : minimo is null ? "data da investidura de Líder desconhecida"
                        : $"início possível a partir de {minimo:yyyy-MM-dd}"));
                break;
            case ClasseDesbravador.LiderMasterAvancado:
                lista.Add(ConclusaoDe(contexto, ClasseDesbravador.LiderMaster));
                break;
        }

        return lista;
    }

    private static PreRequisitoAvaliadoDto AvaliarIdade(Usuario membro, int idadeMinima, DateOnly data, string momento)
    {
        var descricao = $"Ter no mínimo {idadeMinima} anos completos para {momento}";
        if (membro.DataNascimento is not { } nascimento)
        {
            return new PreRequisitoAvaliadoDto(descricao, false, "data de nascimento não cadastrada");
        }

        var idade = data.Year - nascimento.Year - (data < nascimento.AddYears(data.Year - nascimento.Year) ? 1 : 0);
        return new PreRequisitoAvaliadoDto(descricao, idade >= idadeMinima, idade >= idadeMinima ? null : $"{idade} anos em {data:yyyy-MM-dd}");
    }

    private static PreRequisitoAvaliadoDto ConclusaoDe(ContextoMembro contexto, ClasseDesbravador classe)
    {
        var concluida = Concluiu(contexto, classe);
        return new PreRequisitoAvaliadoDto($"Classe {CatalogoClasses.Nome(classe)} concluída", concluida, null);
    }

    private static bool Concluiu(ContextoMembro contexto, ClasseDesbravador classe) =>
        contexto.Classes.Any(c => c.Classe == classe && c.Situacao == SituacaoVinculo.Concluida);

    private async Task<ContextoMembro> ContextoAsync(Usuario membro, CancellationToken ct) => new(membro,
        await db.MembrosClasses.AsNoTracking().Where(c => c.MembroId == membro.Id).ToListAsync(ct),
        await db.MembrosEspecialidades.AsNoTracking().Where(c => c.MembroId == membro.Id).ToListAsync(ct));

    private IQueryable<ProgressoRequisito> ProgressoDe(TipoVinculo tipo, Guid vinculoId) => tipo == TipoVinculo.Classe
        ? db.ProgressoRequisitos.Where(p => p.MembroClasseId == vinculoId)
        : db.ProgressoRequisitos.Where(p => p.MembroEspecialidadeId == vinculoId);

    private static bool ExigenciaAtendida(Requisito requisito, IEnumerable<EvidenciaAvaliacao> evidencias, DateOnly? dataInicio) =>
        requisito.EspecialidadesExigidas is not { } exigidas
        || evidencias.Count(e => ProgressoFormacao.MotivoEvidenciaInvalida(requisito, e, dataInicio) is null) >= exigidas;

    private async Task<List<EvidenciaAvaliacao>> EvidenciasDoRequisitoAsync(Guid membroClasseId, Guid requisitoId, CancellationToken ct) =>
        (await EvidenciasAsync([membroClasseId], ct)).Where(e => e.RequisitoId == requisitoId).Select(e => e.Avaliacao).ToList();

    private async Task<EvidenciaAvaliacao> AvaliacaoEvidenciaAsync(MembroEspecialidade conquista, Guid membroClasseId, CancellationToken ct)
    {
        var especialidade = await db.Especialidades.AsNoTracking().SingleAsync(e => e.Id == conquista.EspecialidadeId, ct);
        var usadaEmOutra = await db.EvidenciasEspecialidades.AnyAsync(e => e.Ativo && e.MembroEspecialidadeId == conquista.Id
            && e.MembroClasseId != membroClasseId, ct);
        return new EvidenciaAvaliacao(conquista.Id, especialidade.Id, especialidade.Nome, especialidade.Area, conquista.Situacao,
            conquista.DataConclusao, usadaEmOutra);
    }

    private async Task<List<EvidenciaCarregada>> EvidenciasAsync(IReadOnlyCollection<Guid> membroClasseIds, CancellationToken ct)
    {
        if (membroClasseIds.Count == 0)
        {
            return [];
        }

        var linhas = await (
            from ev in db.EvidenciasEspecialidades.AsNoTracking()
            join me in db.MembrosEspecialidades.AsNoTracking() on ev.MembroEspecialidadeId equals me.Id
            join e in db.Especialidades.AsNoTracking() on me.EspecialidadeId equals e.Id
            where ev.Ativo && membroClasseIds.Contains(ev.MembroClasseId)
            select new
            {
                ev.MembroClasseId, ev.RequisitoId, me.Id, me.EspecialidadeId, e.Nome, e.Area, me.Situacao, me.DataConclusao,
                UsadaEmOutra = db.EvidenciasEspecialidades.Any(o => o.Ativo && o.MembroEspecialidadeId == me.Id && o.MembroClasseId != ev.MembroClasseId),
            }).ToListAsync(ct);
        return linhas.Select(l => new EvidenciaCarregada(l.MembroClasseId, l.RequisitoId,
            new EvidenciaAvaliacao(l.Id, l.EspecialidadeId, l.Nome, l.Area, l.Situacao, l.DataConclusao, l.UsadaEmOutra))).ToList();
    }

    private async Task<DadosProgresso> CarregarProgressoAsync(IReadOnlyCollection<VinculoFormacao> vinculos, CancellationToken ct)
    {
        var revisoes = vinculos.Where(v => v.RevisaoId.HasValue).Select(v => v.RevisaoId!.Value).Distinct().ToList();
        var requisitos = revisoes.Count == 0
            ? []
            : await db.Requisitos.AsNoTracking().Where(r => revisoes.Contains(r.RevisaoId)).ToListAsync(ct);
        var classes = vinculos.OfType<MembroClasse>().Select(v => v.Id).ToList();
        var especialidades = vinculos.OfType<MembroEspecialidade>().Select(v => v.Id).ToList();
        var progresso = await db.ProgressoRequisitos.AsNoTracking()
            .Where(p => (p.MembroClasseId != null && classes.Contains(p.MembroClasseId.Value))
                || (p.MembroEspecialidadeId != null && especialidades.Contains(p.MembroEspecialidadeId.Value)))
            .ToListAsync(ct);
        var evidencias = await EvidenciasAsync(classes, ct);
        return new DadosProgresso(
            requisitos.ToLookup(r => r.RevisaoId),
            progresso.ToLookup(p => p.MembroClasseId ?? p.MembroEspecialidadeId!.Value),
            evidencias.ToLookup(e => e.MembroClasseId));
    }

    private async Task<List<VinculoResumoDto>> ResumirAsync(IReadOnlyList<VinculoFormacao> vinculos, CancellationToken ct)
    {
        var dados = await CarregarProgressoAsync(vinculos, ct);
        var pessoas = await PessoasAsync([.. vinculos.Select(v => (Guid?)v.MembroId), .. vinculos.Select(v => v.ReconhecimentoRegistradoPorUsuarioId)], ct);
        var especialidadeIds = vinculos.OfType<MembroEspecialidade>().Select(v => v.EspecialidadeId).Distinct().ToList();
        var especialidades = await db.Especialidades.AsNoTracking().Where(e => especialidadeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        return vinculos.Select(v => Resumir(v, pessoas, especialidades, dados.Calcular(v))).ToList();
    }

    private static VinculoResumoDto Resumir(VinculoFormacao v, IReadOnlyDictionary<Guid, PessoaResumoDto> pessoas,
        IReadOnlyDictionary<Guid, Especialidade> especialidades, ResultadoProgresso? resultado)
    {
        var reconhecimento = v.ReconhecimentoRegistradoPorUsuarioId is { } por
            ? new RegistroFormacaoDto(v.DataReconhecimento, pessoas[por], v.ReconhecimentoRegistradoEmUtc!.Value)
            : null;
        var classe = v as MembroClasse;
        return new VinculoResumoDto
        {
            Id = v.Id,
            Tipo = classe is null ? TipoVinculo.Especialidade : TipoVinculo.Classe,
            Membro = pessoas[v.MembroId],
            Classe = classe is null ? null : FormacaoMapping.ClasseResumo(classe.Classe),
            Especialidade = v is MembroEspecialidade e ? FormacaoMapping.EspecialidadeResumo(especialidades[e.EspecialidadeId]) : null,
            Modalidade = classe?.Modalidade,
            Situacao = v.Situacao,
            DataInicio = v.DataInicio,
            DataConclusao = v.DataConclusao,
            Historico = v.Historico,
            Investidura = classe is null ? null : reconhecimento,
            Entrega = classe is null ? reconhecimento : null,
            AguardandoInvestiduraOuEntrega = v.Situacao == SituacaoVinculo.Concluida && !v.Reconhecido,
            Progresso = resultado is null ? null : new ProgressoResumoDto
            {
                UnidadesObrigatorias = resultado.UnidadesObrigatorias,
                UnidadesSatisfeitas = resultado.UnidadesSatisfeitas,
                Percentual = resultado.Percentual,
                RequisitosCumpridos = resultado.RequisitosCumpridos,
                RequisitosPendentes = resultado.RequisitosPendentes,
            },
            Versao = EventoRegras.VersaoParaTexto(v.Versao),
        };
    }

    private async Task<Dictionary<Guid, PessoaResumoDto>> PessoasAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var distintos = ids.OfType<Guid>().Distinct().ToList();
        return await db.Usuarios.AsNoTracking().Where(u => distintos.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new PessoaResumoDto(u.Id, u.Nome), ct);
    }

    private static IQueryable<T> Filtrar<T>(IQueryable<T> consulta, ListarVinculosQuery f) where T : VinculoFormacao
    {
        if (f.MembroId is { } membroId)
        {
            consulta = consulta.Where(v => v.MembroId == membroId);
        }

        if (f.Situacao is { } situacao)
        {
            consulta = consulta.Where(v => v.Situacao == situacao);
        }

        if (f.Historico is { } historico)
        {
            consulta = consulta.Where(v => v.Historico == historico);
        }

        if (f.AguardandoInvestiduraOuEntrega is { } aguardando)
        {
            consulta = aguardando
                ? consulta.Where(v => v.Situacao == SituacaoVinculo.Concluida && v.ReconhecimentoRegistradoPorUsuarioId == null)
                : consulta.Where(v => v.Situacao != SituacaoVinculo.Concluida || v.ReconhecimentoRegistradoPorUsuarioId != null);
        }

        var de = f.De;
        var ate = f.Ate;
        return f.Periodo switch
        {
            PeriodoFormacao.Inicio => consulta.Where(v => v.DataInicio != null && (de == null || v.DataInicio >= de) && (ate == null || v.DataInicio <= ate)),
            PeriodoFormacao.Conclusao => consulta.Where(v => v.DataConclusao != null && (de == null || v.DataConclusao >= de) && (ate == null || v.DataConclusao <= ate)),
            PeriodoFormacao.InvestiduraOuEntrega => consulta.Where(v => v.DataReconhecimento != null
                && (de == null || v.DataReconhecimento >= de) && (ate == null || v.DataReconhecimento <= ate)),
            _ => consulta,
        };
    }

    private IOrderedQueryable<T> Ordenar<T>(IQueryable<T> consulta, OrdenacaoVinculos ordem) where T : VinculoFormacao
    {
        var usuarios = db.Usuarios;
        var porData = ordem switch
        {
            OrdenacaoVinculos.Inicio => consulta.OrderBy(v => v.DataInicio),
            OrdenacaoVinculos.Conclusao => consulta.OrderBy(v => v.DataConclusao),
            OrdenacaoVinculos.InvestiduraOuEntrega => consulta.OrderBy(v => v.DataReconhecimento),
            _ => null,
        };
        return porData is null
            ? consulta.OrderBy(v => usuarios.Where(u => u.Id == v.MembroId).Select(u => u.Nome).FirstOrDefault())
            : porData.ThenBy(v => usuarios.Where(u => u.Id == v.MembroId).Select(u => u.Nome).FirstOrDefault());
    }

    private static JsonElement Json(string json)
    {
        using var documento = JsonDocument.Parse(json);
        return documento.RootElement.Clone();
    }
}
