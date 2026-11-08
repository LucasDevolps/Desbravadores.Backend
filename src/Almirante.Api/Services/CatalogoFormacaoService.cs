using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>Catálogo fixo de classes, catálogo de especialidades e revisões de requisitos (issue #125).</summary>
public sealed class CatalogoFormacaoService(AlmiranteDbContext db, TimeProvider clock, ILogger<CatalogoFormacaoService> logger)
{
    public async Task<ClassesResponse> ListarClassesAsync(CancellationToken ct)
    {
        var vigentes = await RevisoesVigentesDeClassesAsync(ct);
        return new ClassesResponse(
            CatalogoClasses.Todas.Select(d => FormacaoMapping.Classe(d, vigentes.GetValueOrDefault(d.Classe))).ToList(),
            CatalogoClasses.Referencias.Select(r => new ReferenciaOficialDto(r.Titulo, r.Url, r.Data)).ToList());
    }

    public async Task<ClasseDto?> ObterClasseAsync(ClasseDesbravador classe, CancellationToken ct)
    {
        if (!CatalogoClasses.Existe(classe))
        {
            return null;
        }

        var vigente = await db.RevisoesRequisitos.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Classe == classe && r.Situacao == SituacaoRevisao.Publicada, ct);
        return FormacaoMapping.Classe(CatalogoClasses.Obter(classe), vigente);
    }

    public async Task<IReadOnlyList<RevisaoResumoDto>?> ListarRevisoesAsync(ListarRevisoesQuery query, CancellationToken ct)
    {
        if (query.Classe is { } classe && !CatalogoClasses.Existe(classe))
        {
            return null;
        }

        if (query.EspecialidadeId is { } especialidadeId && !await db.Especialidades.AnyAsync(e => e.Id == especialidadeId, ct))
        {
            return null;
        }

        var revisoes = db.RevisoesRequisitos.AsNoTracking()
            .Where(r => query.Classe.HasValue ? r.Classe == query.Classe : r.EspecialidadeId == query.EspecialidadeId);
        if (!query.IncluirNaoPublicadas)
        {
            revisoes = revisoes.Where(r => r.Situacao == SituacaoRevisao.Publicada || r.Situacao == SituacaoRevisao.Substituida);
        }

        var lista = await revisoes.OrderByDescending(r => r.CriadoEmUtc).ThenBy(r => r.Id).ToListAsync(ct);
        return lista.Select(FormacaoMapping.RevisaoResumo).ToList();
    }

    public async Task<PaginaDto<EspecialidadeDto>> ListarEspecialidadesAsync(ListarEspecialidadesQuery query, CancellationToken ct)
    {
        var especialidades = db.Especialidades.AsNoTracking();
        if (!query.IncluirInativas)
        {
            especialidades = especialidades.Where(e => e.Ativo);
        }

        if (FormacaoBanco.Opcional(query.Busca) is { } busca)
        {
            especialidades = especialidades.Where(e => e.Nome.Contains(busca) || (e.CodigoOficial != null && e.CodigoOficial.Contains(busca)));
        }

        if (FormacaoBanco.Opcional(query.Area) is { } area)
        {
            especialidades = especialidades.Where(e => e.Area == area);
        }

        if (query.Modalidade is { } modalidade)
        {
            especialidades = especialidades.Where(e => e.Modalidade == modalidade);
        }

        var total = await especialidades.CountAsync(ct);
        var pagina = await especialidades.OrderBy(e => e.Area).ThenBy(e => e.Nome).ThenBy(e => e.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        var dtos = await MapearEspecialidadesAsync(pagina, ct);
        return FormacaoMapping.Pagina(dtos, total, query.Page, query.PageSize);
    }

    public async Task<EspecialidadeDto?> ObterEspecialidadeAsync(Guid id, CancellationToken ct)
    {
        var especialidade = await db.Especialidades.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        return especialidade is null ? null : (await MapearEspecialidadesAsync([especialidade], ct))[0];
    }

    public async Task<EspecialidadeDto> CadastrarEspecialidadeAsync(CadastrarEspecialidadeRequest request, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await FormacaoBanco.ExecutarAsync(db, logger, request.UsuarioId, request.IpResponsavel, "CADASTRAR_ESPECIALIDADE", null, async _ =>
        {
            var agora = clock.GetUtcNow().UtcDateTime;
            var especialidade = new Especialidade
            {
                Id = id,
                Nome = request.Nome!.Trim(),
                Area = request.Area!.Trim(),
                FonteOficial = request.FonteOficial!.Trim(),
                CriadoPorUsuarioId = request.UsuarioId,
                CriadoEmUtc = agora,
            };
            AplicarConteudo(especialidade, request);
            await ConferirDuplicidadeAsync(especialidade, ct);
            db.Especialidades.Add(especialidade);
            await SincronizarPreRequisitosAsync(especialidade.Id, request.PreRequisitos ?? [], request.UsuarioId, agora, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }, DuplicidadeEspecialidade(), ct);

        return (await ObterEspecialidadeAsync(id, ct))!;
    }

    public async Task<EspecialidadeDto> AtualizarEspecialidadeAsync(AtualizarEspecialidadeRequest request, CancellationToken ct)
    {
        await FormacaoBanco.ExecutarAsync(db, logger, request.UsuarioId, request.IpResponsavel, "ATUALIZAR_ESPECIALIDADE", null, async _ =>
        {
            var especialidade = await CarregarEspecialidadeAsync(request.Id, ct);
            FormacaoBanco.ConferirVersao(especialidade.Versao, request.Versao);
            var agora = clock.GetUtcNow().UtcDateTime;
            especialidade.Nome = request.Nome!.Trim();
            especialidade.Area = request.Area!.Trim();
            especialidade.FonteOficial = request.FonteOficial!.Trim();
            AplicarConteudo(especialidade, request);
            especialidade.AtualizadoPorUsuarioId = request.UsuarioId;
            especialidade.AtualizadoEmUtc = agora;
            await ConferirDuplicidadeAsync(especialidade, ct);
            await SincronizarPreRequisitosAsync(especialidade.Id, request.PreRequisitos ?? [], request.UsuarioId, agora, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }, DuplicidadeEspecialidade(), ct);

        return (await ObterEspecialidadeAsync(request.Id, ct))!;
    }

    public async Task<EspecialidadeDto> AlterarSituacaoEspecialidadeAsync(AlterarSituacaoEspecialidadeRequest request, CancellationToken ct)
    {
        var acao = request.Ativar ? "REATIVAR_ESPECIALIDADE" : "INATIVAR_ESPECIALIDADE";
        var motivo = request.Ativar ? null : request.Motivo!.Trim();
        await FormacaoBanco.ExecutarAsync(db, logger, request.UsuarioId, request.IpResponsavel, acao, motivo, async _ =>
        {
            var especialidade = await CarregarEspecialidadeAsync(request.Id, ct);
            FormacaoBanco.ConferirVersao(especialidade.Versao, request.Versao);
            if (especialidade.Ativo == request.Ativar)
            {
                throw ApiProblemException.Conflict(request.Ativar ? "A especialidade já está ativa." : "A especialidade já está inativa.");
            }

            especialidade.Ativo = request.Ativar;
            especialidade.MotivoInativacao = motivo;
            especialidade.AtualizadoPorUsuarioId = request.UsuarioId;
            especialidade.AtualizadoEmUtc = clock.GetUtcNow().UtcDateTime;
            if (request.Ativar)
            {
                await ConferirDuplicidadeAsync(especialidade, ct);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, DuplicidadeEspecialidade(), ct);

        return (await ObterEspecialidadeAsync(request.Id, ct))!;
    }

    public async Task<RevisaoRequisitosDto?> ObterRevisaoAsync(ObterRevisaoQuery query, CancellationToken ct)
    {
        var revisao = await db.RevisoesRequisitos.AsNoTracking().Include(r => r.Requisitos).SingleOrDefaultAsync(r => r.Id == query.Id, ct);
        if (revisao is null || (!query.IncluirNaoPublicadas && revisao.Situacao is SituacaoRevisao.Rascunho or SituacaoRevisao.Descartada))
        {
            return null;
        }

        var ids = revisao.Requisitos.Where(r => r.EspecialidadeExigidaId.HasValue).Select(r => r.EspecialidadeExigidaId!.Value).ToList();
        if (revisao.EspecialidadeId is { } especialidadeId)
        {
            ids.Add(especialidadeId);
        }

        var especialidades = await db.Especialidades.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        return new RevisaoRequisitosDto
        {
            Id = revisao.Id,
            Classe = revisao.Classe is { } classe ? FormacaoMapping.ClasseResumo(classe) : null,
            Especialidade = revisao.EspecialidadeId is { } eid ? FormacaoMapping.EspecialidadeResumo(especialidades[eid]) : null,
            Edicao = revisao.Edicao,
            FonteOficial = revisao.FonteOficial,
            Observacoes = revisao.Observacoes,
            Situacao = revisao.Situacao,
            MotivoDescarte = revisao.MotivoDescarte,
            CriadaEmUtc = revisao.CriadoEmUtc,
            PublicadaEmUtc = revisao.PublicadaEmUtc,
            UnidadesObrigatorias = revisao.Requisitos.Count(r => r.RequisitoPaiId is null && r.Obrigatorio),
            Requisitos = FormacaoMapping.Requisitos(revisao.Requisitos.ToList(), especialidades),
            Versao = EventoRegras.VersaoParaTexto(revisao.Versao),
        };
    }

    public async Task<RevisaoRequisitosDto> CriarRevisaoAsync(CriarRevisaoRequest request, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await FormacaoBanco.ExecutarAsync(db, logger, request.UsuarioId, request.IpResponsavel, "CRIAR_REVISAO", null, async _ =>
        {
            if (request.EspecialidadeId is { } especialidadeId)
            {
                var especialidade = await db.Especialidades.AsNoTracking().SingleOrDefaultAsync(e => e.Id == especialidadeId, ct)
                    ?? throw ApiProblemException.NotFound("Especialidade não encontrada.");
                if (!especialidade.Ativo)
                {
                    throw ApiProblemException.Conflict("Especialidade inativa.", "Reative a especialidade antes de cadastrar requisitos.");
                }
            }

            var exigidas = Achatar(request.Requisitos!).Select(r => r.ExigenciaEspecialidade?.EspecialidadeId).OfType<Guid>().Distinct().ToList();
            var ativas = await db.Especialidades.Where(e => exigidas.Contains(e.Id) && e.Ativo).Select(e => e.Id).ToListAsync(ct);
            if (ativas.Count != exigidas.Count)
            {
                throw ApiProblemException.Invalid("Requisitos", "exigenciaEspecialidade.especialidadeId contém especialidade inexistente ou inativa: " +
                    string.Join(", ", exigidas.Except(ativas)) + ".");
            }

            var revisao = new RevisaoRequisitos
            {
                Id = id,
                Classe = request.Classe,
                EspecialidadeId = request.EspecialidadeId,
                Edicao = request.Edicao!.Trim(),
                FonteOficial = request.FonteOficial!.Trim(),
                Observacoes = FormacaoBanco.Opcional(request.Observacoes),
                CriadoPorUsuarioId = request.UsuarioId,
                CriadoEmUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.RevisoesRequisitos.Add(revisao);
            AdicionarRequisitos(revisao.Id, null, request.Requisitos!, RegraGrupo.Todos);
            await db.SaveChangesAsync(ct);
            return true;
        }, ApiProblemException.Conflict("Conflito ao cadastrar a revisão."), ct);

        return (await ObterRevisaoAsync(new ObterRevisaoQuery(id, true), ct))!;
    }

    public async Task<RevisaoRequisitosDto> AlterarSituacaoRevisaoAsync(AlterarSituacaoRevisaoRequest request, CancellationToken ct)
    {
        var acao = request.Publicar ? "PUBLICAR_REVISAO" : "DESCARTAR_REVISAO";
        var motivo = request.Publicar ? null : request.Motivo!.Trim();
        await FormacaoBanco.ExecutarAsync(db, logger, request.UsuarioId, request.IpResponsavel, acao, motivo, async _ =>
        {
            await FormacaoBanco.BloquearAsync(db, "requisitos_revisoes", request.Id, ct);
            var revisao = await db.RevisoesRequisitos.SingleOrDefaultAsync(r => r.Id == request.Id, ct)
                ?? throw ApiProblemException.NotFound("Revisão não encontrada.");
            FormacaoBanco.ConferirVersao(revisao.Versao, request.Versao);
            if (revisao.Situacao != SituacaoRevisao.Rascunho)
            {
                throw ApiProblemException.Conflict("Somente revisões em rascunho podem ser publicadas ou descartadas.",
                    "Revisões publicadas são imutáveis: uma alteração oficial gera nova revisão.");
            }

            var agora = clock.GetUtcNow().UtcDateTime;
            if (request.Publicar)
            {
                var anterior = await db.RevisoesRequisitos.SingleOrDefaultAsync(r => r.Situacao == SituacaoRevisao.Publicada
                    && (revisao.Classe.HasValue ? r.Classe == revisao.Classe : r.EspecialidadeId == revisao.EspecialidadeId), ct);
                if (anterior is not null)
                {
                    anterior.Situacao = SituacaoRevisao.Substituida;
                    anterior.AtualizadoPorUsuarioId = request.UsuarioId;
                    anterior.AtualizadoEmUtc = agora;
                    // Antes da nova: o índice único filtrado admite uma só publicada por classe/especialidade a cada instrução.
                    await db.SaveChangesAsync(ct);
                }

                revisao.Situacao = SituacaoRevisao.Publicada;
                revisao.PublicadaPorUsuarioId = request.UsuarioId;
                revisao.PublicadaEmUtc = agora;
            }
            else
            {
                revisao.Situacao = SituacaoRevisao.Descartada;
                revisao.MotivoDescarte = motivo;
            }

            revisao.AtualizadoPorUsuarioId = request.UsuarioId;
            revisao.AtualizadoEmUtc = agora;
            await db.SaveChangesAsync(ct);
            return true;
        }, ApiProblemException.Conflict("Outra revisão foi publicada simultaneamente.", "Consulte as revisões e tente novamente."), ct);

        return (await ObterRevisaoAsync(new ObterRevisaoQuery(request.Id, true), ct))!;
    }

    private async Task<Dictionary<ClasseDesbravador, RevisaoRequisitos>> RevisoesVigentesDeClassesAsync(CancellationToken ct) =>
        (await db.RevisoesRequisitos.AsNoTracking().Where(r => r.Classe != null && r.Situacao == SituacaoRevisao.Publicada).ToListAsync(ct))
            .ToDictionary(r => r.Classe!.Value);

    private async Task<List<EspecialidadeDto>> MapearEspecialidadesAsync(IReadOnlyList<Especialidade> especialidades, CancellationToken ct)
    {
        var ids = especialidades.Select(e => e.Id).ToList();
        var preRequisitos = await (
            from p in db.EspecialidadesPreRequisitos.AsNoTracking()
            join e in db.Especialidades.AsNoTracking() on p.PreRequisitoId equals e.Id
            where p.Ativo && ids.Contains(p.EspecialidadeId)
            select new { p.EspecialidadeId, PreRequisito = e }).ToListAsync(ct);
        var porEspecialidade = preRequisitos.ToLookup(p => p.EspecialidadeId, p => p.PreRequisito);
        var vigentes = await db.RevisoesRequisitos.AsNoTracking()
            .Where(r => r.EspecialidadeId != null && ids.Contains(r.EspecialidadeId.Value) && r.Situacao == SituacaoRevisao.Publicada)
            .ToDictionaryAsync(r => r.EspecialidadeId!.Value, ct);

        return especialidades.Select(e => new EspecialidadeDto
        {
            Id = e.Id,
            Nome = e.Nome,
            CodigoOficial = e.CodigoOficial,
            Area = e.Area,
            Descricao = e.Descricao,
            NivelDificuldade = e.NivelDificuldade,
            Modalidade = e.Modalidade,
            PreRequisitos = porEspecialidade[e.Id].OrderBy(p => p.Nome).Select(FormacaoMapping.EspecialidadeResumo).ToList(),
            PreRequisitosTexto = e.PreRequisitosTexto,
            ReferenciaDistintivo = e.ReferenciaDistintivo,
            FonteOficial = e.FonteOficial,
            EdicaoRevisao = e.EdicaoRevisao,
            Ativo = e.Ativo,
            MotivoInativacao = e.MotivoInativacao,
            RevisaoVigente = vigentes.TryGetValue(e.Id, out var r) ? FormacaoMapping.RevisaoResumo(r) : null,
            CriadoEmUtc = e.CriadoEmUtc,
            AtualizadoEmUtc = e.AtualizadoEmUtc,
            Versao = EventoRegras.VersaoParaTexto(e.Versao),
        }).ToList();
    }

    private async Task<Especialidade> CarregarEspecialidadeAsync(Guid id, CancellationToken ct)
    {
        await FormacaoBanco.BloquearAsync(db, "especialidades", id, ct);
        return await db.Especialidades.SingleOrDefaultAsync(e => e.Id == id, ct)
            ?? throw ApiProblemException.NotFound("Especialidade não encontrada.");
    }

    private static void AplicarConteudo(Especialidade especialidade, EspecialidadeConteudoRequest request)
    {
        especialidade.CodigoOficial = FormacaoBanco.Opcional(request.CodigoOficial);
        especialidade.Descricao = FormacaoBanco.Opcional(request.Descricao);
        especialidade.NivelDificuldade = request.NivelDificuldade;
        especialidade.Modalidade = request.Modalidade;
        especialidade.PreRequisitosTexto = FormacaoBanco.Opcional(request.PreRequisitosTexto);
        especialidade.ReferenciaDistintivo = FormacaoBanco.Opcional(request.ReferenciaDistintivo);
        especialidade.EdicaoRevisao = FormacaoBanco.Opcional(request.EdicaoRevisao);
    }

    /// <summary>Código oficial é único no catálogo (inclusive inativas); nome é único entre as ativas da mesma área e modalidade.</summary>
    private async Task ConferirDuplicidadeAsync(Especialidade e, CancellationToken ct)
    {
        if (e.CodigoOficial is { } codigo && await db.Especialidades.AnyAsync(x => x.Id != e.Id && x.CodigoOficial == codigo, ct))
        {
            throw ApiProblemException.Conflict("Código oficial já cadastrado.", $"Já existe especialidade com o código {codigo} (ativa ou inativa).");
        }

        if (e.Ativo && await db.Especialidades.AnyAsync(x => x.Id != e.Id && x.Ativo && x.Nome == e.Nome && x.Area == e.Area && x.Modalidade == e.Modalidade, ct))
        {
            throw ApiProblemException.Conflict("Especialidade já cadastrada.",
                "Já existe especialidade ativa com o mesmo nome, área e modalidade.");
        }
    }

    private async Task SincronizarPreRequisitosAsync(Guid especialidadeId, IReadOnlyList<Guid> desejados, Guid usuarioId, DateTime agora, CancellationToken ct)
    {
        if (desejados.Contains(especialidadeId))
        {
            throw ApiProblemException.Invalid("PreRequisitos", "Uma especialidade não pode ser pré-requisito de si mesma.");
        }

        var novos = desejados.Distinct().ToList();
        var existentes = await db.EspecialidadesPreRequisitos.Where(p => p.EspecialidadeId == especialidadeId).ToListAsync(ct);
        var adicionados = novos.Where(id => !existentes.Any(p => p.Ativo && p.PreRequisitoId == id)).ToList();
        if (adicionados.Count > 0)
        {
            var ativos = await db.Especialidades.Where(e => adicionados.Contains(e.Id) && e.Ativo).Select(e => e.Id).ToListAsync(ct);
            if (ativos.Count != adicionados.Count)
            {
                throw ApiProblemException.Invalid("PreRequisitos", "preRequisitos contém especialidade inexistente ou inativa: " +
                    string.Join(", ", adicionados.Except(ativos)) + ".");
            }

            var arestas = await db.EspecialidadesPreRequisitos.AsNoTracking().Where(p => p.Ativo && p.EspecialidadeId != especialidadeId)
                .Select(p => new { p.EspecialidadeId, p.PreRequisitoId }).ToListAsync(ct);
            var grafo = arestas.ToLookup(a => a.EspecialidadeId, a => a.PreRequisitoId);
            if (adicionados.Any(inicio => Alcanca(grafo, inicio, especialidadeId)))
            {
                throw ApiProblemException.Invalid("PreRequisitos", "Os pré-requisitos formariam um ciclo entre especialidades.");
            }
        }

        foreach (var existente in existentes)
        {
            var ativo = novos.Contains(existente.PreRequisitoId);
            if (existente.Ativo != ativo)
            {
                existente.Ativo = ativo;
                existente.AtualizadoPorUsuarioId = usuarioId;
                existente.AtualizadoEmUtc = agora;
            }
        }

        foreach (var id in novos.Where(id => existentes.All(p => p.PreRequisitoId != id)))
        {
            db.EspecialidadesPreRequisitos.Add(new EspecialidadePreRequisito
            {
                Id = Guid.NewGuid(), EspecialidadeId = especialidadeId, PreRequisitoId = id, CriadoPorUsuarioId = usuarioId, CriadoEmUtc = agora,
            });
        }
    }

    private static bool Alcanca(ILookup<Guid, Guid> grafo, Guid inicio, Guid alvo)
    {
        var visitados = new HashSet<Guid>();
        var pilha = new Stack<Guid>([inicio]);
        while (pilha.TryPop(out var atual))
        {
            if (atual == alvo)
            {
                return true;
            }

            if (visitados.Add(atual))
            {
                foreach (var proximo in grafo[atual])
                {
                    pilha.Push(proximo);
                }
            }
        }

        return false;
    }

    private void AdicionarRequisitos(Guid revisaoId, Guid? paiId, IReadOnlyList<RequisitoRequest> itens, RegraGrupo regraDoPai)
    {
        for (var i = 0; i < itens.Count; i++)
        {
            var item = itens[i];
            var exigencia = item.ExigenciaEspecialidade;
            var requisito = new Requisito
            {
                Id = Guid.NewGuid(),
                RevisaoId = revisaoId,
                RequisitoPaiId = paiId,
                Codigo = item.Codigo!.Trim(),
                Secao = FormacaoBanco.Opcional(item.Secao),
                Ordem = i + 1,
                Descricao = item.Descricao!.Trim(),
                Obrigatorio = regraDoPai == RegraGrupo.Todos && (item.Obrigatorio ?? true),
                Regra = item.Regra,
                QuantidadeMinima = item.Regra == RegraGrupo.Alternativas ? item.QuantidadeMinima : null,
                ReferenciaOficial = FormacaoBanco.Opcional(item.ReferenciaOficial),
                EspecialidadesExigidas = exigencia?.Quantidade,
                EspecialidadeExigidaId = exigencia?.EspecialidadeId,
                AreaEspecialidadeExigida = FormacaoBanco.Opcional(exigencia?.Area),
                SomenteEspecialidadeNaoRealizada = exigencia?.NaoRealizadaAnteriormente ?? false,
            };
            db.Requisitos.Add(requisito);
            if (item.Subitens is { Count: > 0 } subitens)
            {
                AdicionarRequisitos(revisaoId, requisito.Id, subitens, item.Regra!.Value);
            }
        }
    }

    internal static IEnumerable<RequisitoRequest> Achatar(IEnumerable<RequisitoRequest> itens) =>
        itens.SelectMany(i => new[] { i }.Concat(Achatar(i.Subitens ?? [])));

    private static ApiProblemException DuplicidadeEspecialidade() =>
        ApiProblemException.Conflict("Especialidade já cadastrada.", "Código oficial, ou nome na mesma área e modalidade, já existe no catálogo.");
}
