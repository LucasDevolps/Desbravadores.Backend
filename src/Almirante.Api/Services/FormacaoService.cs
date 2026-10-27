using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Acompanhamento individual de classes e especialidades (issue #125): associação, progresso por requisito, conclusão, investidura/entrega,
/// cancelamento, retomada, reabertura e migração de revisão. Toda escrita é transacional, trava o vínculo (UPDLOCK) e tem o estado anterior
/// gravado por trigger em formacao_historico com responsável, IP, ação e justificativa.
/// </summary>
public sealed partial class FormacaoService(AlmiranteDbContext db, TimeProvider clock, DataCivil dataCivil, ILogger<FormacaoService> logger)
{
    public async Task<VinculoDto> AssociarClasseAsync(AssociarClasseRequest r, CancellationToken ct)
    {
        r.Id = Guid.NewGuid();
        var classe = r.Classe!.Value;
        var definicao = CatalogoClasses.Obter(classe);
        var modalidade = r.Modalidade ?? ModalidadeCumprimento.Padrao;
        if (modalidade == ModalidadeCumprimento.Agrupada && !definicao.PermiteModalidadeAgrupada)
        {
            throw ApiProblemException.Invalid("Modalidade", "Classes de liderança não são cumpridas na modalidade agrupada.");
        }

        await FormacaoBanco.ExecutarAsync(db, logger, r.UsuarioId, r.IpResponsavel, "ASSOCIAR_CLASSE", null, async _ =>
        {
            var membro = await MembroParaNovoVinculoAsync(r.MembroId!.Value, ct);
            await ConferirInstrutorAsync(r.InstrutorId, ct);
            var dataInicio = ConferirDataInicio(r.DataInicio!.Value, membro);
            var revisao = await db.RevisoesRequisitos.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Classe == classe && x.Situacao == SituacaoRevisao.Publicada, ct)
                ?? throw ApiProblemException.Conflict("Não há revisão publicada dos requisitos desta classe.",
                    $"Cadastre e publique os requisitos de {definicao.Nome} em /api/RevisoesRequisitos antes de associar membros.");

            var contexto = await ContextoAsync(membro, ct);
            if (contexto.Classes.Any(c => c.Classe == classe && c.Situacao != SituacaoVinculo.Cancelada))
            {
                throw ConflitoVinculoAtivo(TipoVinculo.Classe);
            }

            var pendentes = AvaliarInicio(classe, dataInicio, contexto).Where(p => !p.Atendido).ToList();
            if (pendentes.Count > 0)
            {
                throw new ApiProblemException(StatusCodes.Status409Conflict, "Pré-requisitos de início não atendidos.",
                    string.Join(" ", pendentes.Select(p => p.Descricao + (p.Detalhe is null ? "." : $": {p.Detalhe}."))),
                    extensions: new Dictionary<string, object?> { ["preRequisitos"] = pendentes });
            }

            db.MembrosClasses.Add(new MembroClasse
            {
                Id = r.Id, MembroId = membro.Id, Classe = classe, Modalidade = modalidade, RevisaoId = revisao.Id, DataInicio = dataInicio,
                InstrutorId = r.InstrutorId, Observacoes = FormacaoBanco.Opcional(r.Observacoes),
                CriadoPorUsuarioId = r.UsuarioId, CriadoEmUtc = clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(ct);
            return true;
        }, ConflitoVinculoAtivo(TipoVinculo.Classe), ct);

        return (await ObterAsync(new ObterVinculoQuery(TipoVinculo.Classe, r.Id), ct))!;
    }

    public async Task<VinculoDto> AssociarEspecialidadeAsync(AssociarEspecialidadeRequest r, CancellationToken ct)
    {
        r.Id = Guid.NewGuid();
        await FormacaoBanco.ExecutarAsync(db, logger, r.UsuarioId, r.IpResponsavel, "ASSOCIAR_ESPECIALIDADE", null, async _ =>
        {
            var membro = await MembroParaNovoVinculoAsync(r.MembroId!.Value, ct);
            await ConferirInstrutorAsync(r.InstrutorId, ct);
            var dataInicio = ConferirDataInicio(r.DataInicio!.Value, membro);
            var especialidade = await EspecialidadeAtivaAsync(r.EspecialidadeId!.Value, ct);
            var revisao = await db.RevisoesRequisitos.AsNoTracking()
                .SingleOrDefaultAsync(x => x.EspecialidadeId == especialidade.Id && x.Situacao == SituacaoRevisao.Publicada, ct)
                ?? throw ApiProblemException.Conflict("Não há revisão publicada dos requisitos desta especialidade.",
                    $"Cadastre e publique os requisitos de {especialidade.Nome} em /api/RevisoesRequisitos antes de associar membros.");

            if (await db.MembrosEspecialidades.AnyAsync(v => v.MembroId == membro.Id && v.EspecialidadeId == especialidade.Id
                && v.Situacao != SituacaoVinculo.Cancelada, ct))
            {
                throw ConflitoVinculoAtivo(TipoVinculo.Especialidade);
            }

            db.MembrosEspecialidades.Add(new MembroEspecialidade
            {
                Id = r.Id, MembroId = membro.Id, EspecialidadeId = especialidade.Id, RevisaoId = revisao.Id, DataInicio = dataInicio,
                InstrutorId = r.InstrutorId, Observacoes = FormacaoBanco.Opcional(r.Observacoes),
                CriadoPorUsuarioId = r.UsuarioId, CriadoEmUtc = clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(ct);
            return true;
        }, ConflitoVinculoAtivo(TipoVinculo.Especialidade), ct);

        return (await ObterAsync(new ObterVinculoQuery(TipoVinculo.Especialidade, r.Id), ct))!;
    }

    /// <summary>
    /// Conquista anterior à implantação: entra concluída, sem revisão nem validação de requisitos, com a origem informada e o usuário que
    /// lançou como responsável. Datas desconhecidas ficam null (nunca inventadas); regras de início não se aplicam a fatos passados.
    /// </summary>
    public async Task<VinculoDto> RegistrarHistoricoAsync(RegistrarHistoricoRequest r, CancellationToken ct)
    {
        r.Id = Guid.NewGuid();
        var hoje = dataCivil.Hoje();
        foreach (var (campo, data) in new[] { ("DataInicio", r.DataInicio), ("DataConclusao", r.DataConclusao), ("DataInvestiduraOuEntrega", r.DataInvestiduraOuEntrega) })
        {
            if (data > hoje)
            {
                throw ApiProblemException.Invalid(campo, "Registro histórico não aceita data futura.");
            }
        }

        await FormacaoBanco.ExecutarAsync(db, logger, r.UsuarioId, r.IpResponsavel, "REGISTRAR_HISTORICO", null, async _ =>
        {
            var membro = await db.Usuarios.AsNoTracking().SingleOrDefaultAsync(u => u.Id == r.MembroId, ct)
                ?? throw ApiProblemException.NotFound("Membro não encontrado.");
            var agora = clock.GetUtcNow().UtcDateTime;
            VinculoFormacao vinculo;
            if (r.Tipo == TipoVinculo.Classe)
            {
                var classe = r.Classe!.Value;
                var modalidade = r.Modalidade ?? ModalidadeCumprimento.Padrao;
                if (modalidade == ModalidadeCumprimento.Agrupada && !CatalogoClasses.Obter(classe).PermiteModalidadeAgrupada)
                {
                    throw ApiProblemException.Invalid("Modalidade", "Classes de liderança não são cumpridas na modalidade agrupada.");
                }

                if (await db.MembrosClasses.AnyAsync(v => v.MembroId == membro.Id && v.Classe == classe && v.Situacao != SituacaoVinculo.Cancelada, ct))
                {
                    throw ConflitoVinculoAtivo(TipoVinculo.Classe);
                }

                vinculo = new MembroClasse { Classe = classe, Modalidade = modalidade };
                db.MembrosClasses.Add((MembroClasse)vinculo);
            }
            else
            {
                var especialidade = await EspecialidadeAtivaAsync(r.EspecialidadeId!.Value, ct);
                if (await db.MembrosEspecialidades.AnyAsync(v => v.MembroId == membro.Id && v.EspecialidadeId == especialidade.Id
                    && v.Situacao != SituacaoVinculo.Cancelada, ct))
                {
                    throw ConflitoVinculoAtivo(TipoVinculo.Especialidade);
                }

                vinculo = new MembroEspecialidade { EspecialidadeId = especialidade.Id };
                db.MembrosEspecialidades.Add((MembroEspecialidade)vinculo);
            }

            vinculo.Id = r.Id;
            vinculo.MembroId = membro.Id;
            vinculo.Historico = true;
            vinculo.OrigemHistorica = r.OrigemHistorica!.Trim();
            vinculo.Observacoes = FormacaoBanco.Opcional(r.Observacoes);
            vinculo.DataInicio = r.DataInicio;
            vinculo.Situacao = SituacaoVinculo.Concluida;
            vinculo.DataConclusao = r.DataConclusao;
            vinculo.ConclusaoRegistradaPorUsuarioId = r.UsuarioId;
            vinculo.ConclusaoRegistradaEmUtc = agora;
            if (r.InvestiduraOuEntregaRealizada == true)
            {
                vinculo.DataReconhecimento = r.DataInvestiduraOuEntrega;
                vinculo.ReconhecimentoRegistradoPorUsuarioId = r.UsuarioId;
                vinculo.ReconhecimentoRegistradoEmUtc = agora;
            }

            vinculo.CriadoPorUsuarioId = r.UsuarioId;
            vinculo.CriadoEmUtc = agora;
            await db.SaveChangesAsync(ct);
            return true;
        }, ConflitoVinculoAtivo(r.Tipo), ct);

        return (await ObterAsync(new ObterVinculoQuery(r.Tipo, r.Id), ct))!;
    }

    public Task<VinculoDto> AtualizarAsync(AtualizarVinculoRequest r, CancellationToken ct) =>
        AlterarAsync(r, "ATUALIZAR", null, async (vinculo, agora) =>
        {
            FormacaoBanco.ConferirVersao(vinculo.Versao, r.Versao);
            await ConferirInstrutorAsync(r.InstrutorId == vinculo.InstrutorId ? null : r.InstrutorId, ct);
            vinculo.InstrutorId = r.InstrutorId;
            vinculo.Observacoes = FormacaoBanco.Opcional(r.Observacoes);
            Tocar(vinculo, r.UsuarioId, agora);
        }, ct);

    public Task<VinculoDto> AtualizarRequisitoAsync(AtualizarRequisitoVinculoRequest r, CancellationToken ct) =>
        AlterarAsync(r, "ATUALIZAR_REQUISITO", FormacaoBanco.Opcional(r.Justificativa), async (vinculo, agora) =>
        {
            ExigirEmAndamento(vinculo, "alterar requisitos");
            var requisito = await RequisitoDoVinculoAsync(vinculo, r.RequisitoId, ct);
            if (requisito.Regra is not null)
            {
                throw ApiProblemException.Conflict("Requisito agrupador.",
                    "Grupos são satisfeitos pelos subitens conforme a regra; atualize os subitens.");
            }

            var situacao = r.Situacao!.Value;
            if (r.DataCumprimento is { } cumprimento && (cumprimento < vinculo.DataInicio || cumprimento > dataCivil.Hoje()))
            {
                throw ApiProblemException.Invalid("DataCumprimento", "dataCumprimento deve estar entre o início do vínculo e hoje.");
            }

            var progresso = await ProgressoDe(r.Tipo, vinculo.Id).SingleOrDefaultAsync(p => p.RequisitoId == requisito.Id, ct);
            if (progresso?.Situacao == SituacaoRequisito.Validado && situacao != SituacaoRequisito.Validado
                && FormacaoBanco.Opcional(r.Justificativa) is null)
            {
                throw ApiProblemException.Invalid("Justificativa", "Informe a justificativa para retirar a validação de um requisito.");
            }

            if (situacao == SituacaoRequisito.Validado && requisito.EspecialidadesExigidas is not null)
            {
                var evidencias = await EvidenciasDoRequisitoAsync(vinculo.Id, requisito.Id, ct);
                if (!ExigenciaAtendida(requisito, evidencias, vinculo.DataInicio))
                {
                    throw ApiProblemException.Conflict("Evidências insuficientes.",
                        $"O requisito exige {requisito.EspecialidadesExigidas} especialidade(s) concluída(s) válida(s) vinculada(s) como evidência.");
                }
            }

            if (progresso is null)
            {
                progresso = new ProgressoRequisito
                {
                    Id = Guid.NewGuid(), RequisitoId = requisito.Id, CriadoPorUsuarioId = r.UsuarioId, CriadoEmUtc = agora,
                    MembroClasseId = r.Tipo == TipoVinculo.Classe ? vinculo.Id : null,
                    MembroEspecialidadeId = r.Tipo == TipoVinculo.Especialidade ? vinculo.Id : null,
                };
                db.ProgressoRequisitos.Add(progresso);
            }
            else
            {
                progresso.AtualizadoPorUsuarioId = r.UsuarioId;
                progresso.AtualizadoEmUtc = agora;
            }

            if (situacao == SituacaoRequisito.Validado && progresso.Situacao != SituacaoRequisito.Validado)
            {
                progresso.ValidadoPorUsuarioId = r.UsuarioId;
                progresso.ValidadoEmUtc = agora;
            }
            else if (situacao != SituacaoRequisito.Validado)
            {
                progresso.ValidadoPorUsuarioId = null;
                progresso.ValidadoEmUtc = null;
            }

            progresso.Situacao = situacao;
            progresso.DataCumprimento = r.DataCumprimento;
            progresso.Observacao = FormacaoBanco.Opcional(r.Observacao);
            progresso.ReferenciaEvidencia = FormacaoBanco.Opcional(r.ReferenciaEvidencia);
        }, ct);

    public Task<VinculoDto> VincularEvidenciaAsync(VincularEvidenciaRequest r, CancellationToken ct) =>
        AlterarAsync(r, "VINCULAR_EVIDENCIA", null, async (vinculo, agora) =>
        {
            ExigirEmAndamento(vinculo, "vincular evidências");
            var requisito = await RequisitoDoVinculoAsync(vinculo, r.RequisitoId, ct);
            if (requisito.EspecialidadesExigidas is null)
            {
                throw ApiProblemException.Conflict("O requisito não exige especialidades.");
            }

            await FormacaoBanco.BloquearAsync(db, "membros_especialidades", r.MembroEspecialidadeId!.Value, ct);
            var conquista = await db.MembrosEspecialidades.AsNoTracking()
                .SingleOrDefaultAsync(e => e.Id == r.MembroEspecialidadeId && e.MembroId == vinculo.MembroId, ct)
                ?? throw ApiProblemException.NotFound("Especialidade do membro não encontrada.");
            if (await db.EvidenciasEspecialidades.AnyAsync(e => e.Ativo && e.MembroClasseId == vinculo.Id && e.MembroEspecialidadeId == conquista.Id, ct))
            {
                throw ApiProblemException.Conflict("Especialidade já vinculada a esta classe.",
                    "Uma conquista conta uma única vez por classe: remova o vínculo existente antes de usá-la em outro requisito.");
            }

            var avaliacao = await AvaliacaoEvidenciaAsync(conquista, vinculo.Id, ct);
            if (ProgressoFormacao.MotivoEvidenciaInvalida(requisito, avaliacao, vinculo.DataInicio) is { } motivo)
            {
                throw ApiProblemException.Conflict("Especialidade não serve de evidência para este requisito.", motivo);
            }

            db.EvidenciasEspecialidades.Add(new EvidenciaEspecialidade
            {
                Id = Guid.NewGuid(), MembroClasseId = vinculo.Id, RequisitoId = requisito.Id, MembroEspecialidadeId = conquista.Id,
                CriadoPorUsuarioId = r.UsuarioId, CriadoEmUtc = agora,
            });
        }, ct);

    public Task<VinculoDto> RemoverEvidenciaAsync(RemoverEvidenciaRequest r, CancellationToken ct) =>
        AlterarAsync(r, "REMOVER_EVIDENCIA", r.Justificativa!.Trim(), async (vinculo, agora) =>
        {
            ExigirEmAndamento(vinculo, "remover evidências");
            var requisito = await RequisitoDoVinculoAsync(vinculo, r.RequisitoId, ct);
            var evidencia = await db.EvidenciasEspecialidades.SingleOrDefaultAsync(e => e.Ativo && e.MembroClasseId == vinculo.Id
                && e.RequisitoId == requisito.Id && e.MembroEspecialidadeId == r.MembroEspecialidadeId, ct)
                ?? throw ApiProblemException.NotFound("Evidência não encontrada.");

            var validado = await db.ProgressoRequisitos.AnyAsync(p => p.MembroClasseId == vinculo.Id && p.RequisitoId == requisito.Id
                && p.Situacao == SituacaoRequisito.Validado, ct);
            var restantes = (await EvidenciasDoRequisitoAsync(vinculo.Id, requisito.Id, ct))
                .Where(e => e.MembroEspecialidadeId != evidencia.MembroEspecialidadeId).ToList();
            if (validado && !ExigenciaAtendida(requisito, restantes, vinculo.DataInicio))
            {
                throw ApiProblemException.Conflict("A remoção invalidaria um requisito validado.",
                    "Retire a validação do requisito (com justificativa) antes de remover a evidência.");
            }

            evidencia.Ativo = false;
            evidencia.RemovidaPorUsuarioId = r.UsuarioId;
            evidencia.RemovidaEmUtc = agora;
        }, ct);

    public Task<VinculoDto> RegistrarValidacaoExternaAsync(RegistrarValidacaoExternaRequest r, CancellationToken ct) =>
        AlterarAsync(r, "VALIDACAO_EXTERNA", FormacaoBanco.Opcional(r.Justificativa), async (vinculo, agora) =>
        {
            var classe = (MembroClasse)vinculo;
            var codigo = r.Codigo.Trim().ToUpperInvariant();
            if (CatalogoClasses.Obter(classe.Classe).RequisitosExternos.All(e => e.Codigo != codigo))
            {
                throw ApiProblemException.NotFound("Validação externa não prevista para esta classe.");
            }

            if (vinculo.Situacao == SituacaoVinculo.Cancelada)
            {
                throw ApiProblemException.Conflict("Vínculo cancelado.", "Retome o vínculo antes de registrar validações externas.");
            }

            if (r.Data > dataCivil.Hoje())
            {
                throw ApiProblemException.Invalid("Data", "data não pode ser futura.");
            }

            var validacao = await db.ValidacoesExternas.SingleOrDefaultAsync(v => v.MembroClasseId == vinculo.Id && v.Codigo == codigo, ct);
            if (validacao is { Confirmada: true } && r.Confirmada == false)
            {
                if (FormacaoBanco.Opcional(r.Justificativa) is null)
                {
                    throw ApiProblemException.Invalid("Justificativa", "Informe a justificativa para desfazer uma validação confirmada.");
                }

                if (vinculo.Reconhecido && !vinculo.Historico)
                {
                    throw ApiProblemException.Conflict("A investidura depende desta validação.",
                        "Desfaça a investidura (com justificativa) antes de desfazer a validação externa.");
                }
            }

            if (validacao is null)
            {
                validacao = new ValidacaoExterna { Id = Guid.NewGuid(), MembroClasseId = vinculo.Id, Codigo = codigo };
                db.ValidacoesExternas.Add(validacao);
            }

            validacao.Confirmada = r.Confirmada!.Value;
            validacao.Data = r.Data;
            validacao.Referencia = FormacaoBanco.Opcional(r.Referencia);
            validacao.Observacao = FormacaoBanco.Opcional(r.Observacao);
            validacao.RegistradaPorUsuarioId = r.UsuarioId;
            validacao.RegistradaEmUtc = agora;
        }, ct);

    public Task<VinculoDto> ConcluirAsync(ConcluirVinculoRequest r, CancellationToken ct) =>
        AlterarAsync(r, "CONCLUIR", null, async (vinculo, agora) =>
        {
            FormacaoBanco.ConferirVersao(vinculo.Versao, r.Versao);
            ExigirEmAndamento(vinculo, "concluir");
            var data = r.DataConclusao!.Value;
            if (data < vinculo.DataInicio || data > dataCivil.Hoje())
            {
                throw ApiProblemException.Invalid("DataConclusao", "dataConclusao deve estar entre o início do vínculo e hoje.");
            }

            var avaliacao = await AvaliarVinculoAsync(r.Tipo, vinculo, data, ct);
            if (avaliacao.UltimoCumprimento is { } ultimo && data < ultimo)
            {
                throw ApiProblemException.Invalid("DataConclusao",
                    $"dataConclusao não pode ser anterior ao cumprimento de um requisito validado ({ultimo:yyyy-MM-dd}).");
            }

            if (avaliacao.Bloqueios.Count > 0)
            {
                throw new ApiProblemException(StatusCodes.Status409Conflict, "Conclusão bloqueada.", string.Join(" ", avaliacao.Bloqueios),
                    extensions: new Dictionary<string, object?>
                    {
                        ["requisitosPendentes"] = avaliacao.Resultado?.RequisitosPendentes ?? [],
                        ["preRequisitos"] = avaliacao.PreRequisitos.Where(p => !p.Atendido).ToList(),
                    });
            }

            vinculo.Situacao = SituacaoVinculo.Concluida;
            vinculo.DataConclusao = data;
            vinculo.ConclusaoRegistradaPorUsuarioId = r.UsuarioId;
            vinculo.ConclusaoRegistradaEmUtc = agora;
            Tocar(vinculo, r.UsuarioId, agora);
        }, ct);

    public Task<VinculoDto> RegistrarReconhecimentoAsync(RegistrarReconhecimentoRequest r, CancellationToken ct) =>
        AlterarAsync(r, r.Tipo == TipoVinculo.Classe ? "REGISTRAR_INVESTIDURA" : "REGISTRAR_ENTREGA", null, async (vinculo, agora) =>
        {
            FormacaoBanco.ConferirVersao(vinculo.Versao, r.Versao);
            var nome = NomeReconhecimento(r.Tipo);
            if (vinculo.Situacao != SituacaoVinculo.Concluida)
            {
                throw ApiProblemException.Conflict($"Só um vínculo concluído recebe {nome}.",
                    "Concluir os requisitos e registrar a investidura/entrega são atos separados.");
            }

            if (vinculo.Reconhecido)
            {
                throw ApiProblemException.Conflict($"A {nome} já foi registrada.", $"Para corrigir, desfaça a {nome} (com justificativa) e registre de novo.");
            }

            var data = r.Data!.Value;
            if (data > dataCivil.Hoje() || data < vinculo.DataConclusao || data < vinculo.DataInicio)
            {
                throw ApiProblemException.Invalid("Data", $"A data da {nome} deve estar entre a conclusão e hoje.");
            }

            if (vinculo is MembroClasse classe)
            {
                var pendentes = await AvaliarInvestiduraAsync(classe, data, ct);
                if (pendentes.Count > 0)
                {
                    throw new ApiProblemException(StatusCodes.Status409Conflict, "Investidura bloqueada.",
                        string.Join(" ", pendentes.Select(p => p.Descricao + (p.Detalhe is null ? "." : $": {p.Detalhe}."))),
                        extensions: new Dictionary<string, object?> { ["preRequisitos"] = pendentes });
                }
            }

            vinculo.DataReconhecimento = data;
            vinculo.ReconhecimentoRegistradoPorUsuarioId = r.UsuarioId;
            vinculo.ReconhecimentoRegistradoEmUtc = agora;
            Tocar(vinculo, r.UsuarioId, agora);
        }, ct);

    public Task<VinculoDto> ExecutarOperacaoAsync(OperacaoVinculoRequest r, CancellationToken ct)
    {
        var acao = r.Operacao switch
        {
            OperacaoVinculo.Cancelar => "CANCELAR",
            OperacaoVinculo.Retomar => "RETOMAR",
            OperacaoVinculo.Reabrir => "REABRIR",
            _ => r.Tipo == TipoVinculo.Classe ? "DESFAZER_INVESTIDURA" : "DESFAZER_ENTREGA",
        };
        var justificativa = r.Justificativa!.Trim();
        return AlterarAsync(r, acao, justificativa, async (vinculo, agora) =>
        {
            FormacaoBanco.ConferirVersao(vinculo.Versao, r.Versao);
            switch (r.Operacao)
            {
                case OperacaoVinculo.Cancelar:
                    await CancelarAsync(r.Tipo, vinculo, justificativa, r.UsuarioId, agora, ct);
                    break;
                case OperacaoVinculo.Retomar:
                    await RetomarAsync(r.Tipo, vinculo, ct);
                    break;
                case OperacaoVinculo.Reabrir:
                    if (vinculo.Situacao != SituacaoVinculo.Concluida || vinculo.Historico)
                    {
                        throw ApiProblemException.Conflict("Só um vínculo concluído pelo fluxo normal pode ser reaberto.",
                            "Registro histórico não tem requisitos acompanhados: cancele-o (com justificativa) e registre novamente se necessário.");
                    }

                    await ConferirDependentesAsync(r.Tipo, vinculo, ct);
                    LimparConclusao(vinculo);
                    vinculo.Situacao = SituacaoVinculo.EmAndamento;
                    break;
                default:
                    if (!vinculo.Reconhecido)
                    {
                        throw ApiProblemException.Conflict($"Não há {NomeReconhecimento(r.Tipo)} registrada.");
                    }

                    if (vinculo is MembroClasse { Classe: ClasseDesbravador.Lider }
                        && await db.MembrosClasses.AnyAsync(c => c.MembroId == vinculo.MembroId && c.Classe == ClasseDesbravador.LiderMaster
                            && c.Situacao != SituacaoVinculo.Cancelada, ct))
                    {
                        throw ApiProblemException.Conflict("A investidura de Líder sustenta o início de Líder Máster.",
                            "Cancele o vínculo de Líder Máster antes de desfazer a investidura de Líder.");
                    }

                    LimparReconhecimento(vinculo);
                    break;
            }

            Tocar(vinculo, r.UsuarioId, agora);
        }, ct);
    }

    public Task<VinculoDto> MigrarRevisaoAsync(MigrarRevisaoRequest r, CancellationToken ct) =>
        AlterarAsync(r, "MIGRAR_REVISAO", r.Justificativa!.Trim(), async (vinculo, agora) =>
        {
            FormacaoBanco.ConferirVersao(vinculo.Versao, r.Versao);
            ExigirEmAndamento(vinculo, "migrar de revisão");
            var destino = await db.RevisoesRequisitos.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.RevisaoId, ct)
                ?? throw ApiProblemException.NotFound("Revisão não encontrada.");
            // Só existem os dois tipos de vínculo (classe e especialidade).
            var mesmoAlvo = vinculo is MembroClasse c
                ? destino.Classe == c.Classe
                : destino.EspecialidadeId == ((MembroEspecialidade)vinculo).EspecialidadeId;
            if (!mesmoAlvo || destino.Situacao != SituacaoRevisao.Publicada || destino.Id == vinculo.RevisaoId)
            {
                throw ApiProblemException.Conflict("Revisão de destino inválida.",
                    "A migração exige a revisão publicada (vigente) da mesma classe/especialidade, diferente da atual.");
            }

            // O progresso da revisão anterior fica preservado (ligado aos requisitos dela); nenhuma validação é copiada para a nova.
            foreach (var evidencia in await db.EvidenciasEspecialidades.Where(e => e.Ativo && e.MembroClasseId == vinculo.Id).ToListAsync(ct))
            {
                evidencia.Ativo = false;
                evidencia.RemovidaPorUsuarioId = r.UsuarioId;
                evidencia.RemovidaEmUtc = agora;
            }

            vinculo.RevisaoId = destino.Id;
            Tocar(vinculo, r.UsuarioId, agora);
        }, ct);

    private async Task CancelarAsync(TipoVinculo tipo, VinculoFormacao vinculo, string justificativa, Guid usuarioId, DateTime agora, CancellationToken ct)
    {
        if (vinculo.Situacao == SituacaoVinculo.Cancelada)
        {
            throw ApiProblemException.Conflict("O vínculo já está cancelado.");
        }

        if (vinculo.Situacao == SituacaoVinculo.Concluida)
        {
            if (!vinculo.Historico)
            {
                throw ApiProblemException.Conflict("Vínculo concluído.", "Reabra o vínculo (com justificativa) antes de cancelá-lo.");
            }

            await ConferirDependentesAsync(tipo, vinculo, ct);
            LimparConclusao(vinculo);
        }

        vinculo.Situacao = SituacaoVinculo.Cancelada;
        vinculo.MotivoCancelamento = justificativa;
        vinculo.CanceladoPorUsuarioId = usuarioId;
        vinculo.CanceladoEmUtc = agora;
    }

    private async Task RetomarAsync(TipoVinculo tipo, VinculoFormacao vinculo, CancellationToken ct)
    {
        if (vinculo.Situacao != SituacaoVinculo.Cancelada || vinculo.Historico)
        {
            throw ApiProblemException.Conflict("Só um vínculo cancelado do fluxo normal pode ser retomado.",
                "Registro histórico cancelado não é retomado: registre-o novamente.");
        }

        var e = vinculo as MembroEspecialidade;
        var duplicado = vinculo is MembroClasse c
            ? await db.MembrosClasses.AnyAsync(x => x.Id != c.Id && x.MembroId == c.MembroId && x.Classe == c.Classe
                && x.Situacao != SituacaoVinculo.Cancelada, ct)
            : await db.MembrosEspecialidades.AnyAsync(x => x.Id != e!.Id && x.MembroId == e.MembroId
                && x.EspecialidadeId == e.EspecialidadeId && x.Situacao != SituacaoVinculo.Cancelada, ct);
        if (duplicado)
        {
            throw ConflitoVinculoAtivo(tipo);
        }

        vinculo.Situacao = SituacaoVinculo.EmAndamento;
        vinculo.MotivoCancelamento = null;
        vinculo.CanceladoPorUsuarioId = null;
        vinculo.CanceladoEmUtc = null;
    }

    /// <summary>
    /// Uma conclusão não volta silenciosamente a "em andamento" quando outra conclusão depende dela: recusa com a lista do que precisa ser
    /// reaberto antes (classe pré-requisito de outra classe concluída; especialidade evidência ou pré-requisito de algo concluído).
    /// </summary>
    private async Task ConferirDependentesAsync(TipoVinculo tipo, VinculoFormacao vinculo, CancellationToken ct)
    {
        var dependentes = new List<string>();
        if (vinculo is MembroClasse classe)
        {
            var dependem = CatalogoClasses.Todas.Where(d => CatalogoClasses.PreRequisitosDeConclusao(d.Classe).Contains(classe.Classe))
                .Select(d => d.Classe).ToList();
            var concluidas = await db.MembrosClasses.AsNoTracking()
                .Where(c => c.MembroId == vinculo.MembroId && c.Situacao == SituacaoVinculo.Concluida && dependem.Contains(c.Classe))
                .Select(c => new { c.Id, c.Classe }).ToListAsync(ct);
            dependentes.AddRange(concluidas.Select(c => $"Classe {CatalogoClasses.Nome(c.Classe)} concluída (vínculo {c.Id})"));
        }
        else if (vinculo is MembroEspecialidade especialidade)
        {
            var evidencias = await (
                from e in db.EvidenciasEspecialidades.AsNoTracking()
                join c in db.MembrosClasses.AsNoTracking() on e.MembroClasseId equals c.Id
                where e.Ativo && e.MembroEspecialidadeId == vinculo.Id && c.Situacao == SituacaoVinculo.Concluida
                select new { c.Id, c.Classe }).ToListAsync(ct);
            dependentes.AddRange(evidencias.Select(c => $"Classe {CatalogoClasses.Nome(c.Classe)} concluída usa esta especialidade como evidência (vínculo {c.Id})"));

            var exigem = await (
                from v in db.MembrosEspecialidades.AsNoTracking()
                join p in db.EspecialidadesPreRequisitos.AsNoTracking() on v.EspecialidadeId equals p.EspecialidadeId
                join e in db.Especialidades.AsNoTracking() on v.EspecialidadeId equals e.Id
                where v.MembroId == vinculo.MembroId && v.Situacao == SituacaoVinculo.Concluida && p.Ativo
                    && p.PreRequisitoId == especialidade.EspecialidadeId
                select new { v.Id, e.Nome }).ToListAsync(ct);
            dependentes.AddRange(exigem.Select(e => $"Especialidade {e.Nome} concluída tem esta como pré-requisito (vínculo {e.Id})"));
        }

        if (dependentes.Count > 0)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "Outras conclusões dependem desta.",
                $"Reabra antes: {string.Join("; ", dependentes)}. A {NomeTipo(tipo)} não pode deixar de estar concluída enquanto sustenta essas conclusões.",
                extensions: new Dictionary<string, object?> { ["dependentes"] = dependentes });
        }
    }

    private async Task<VinculoDto> AlterarAsync(ComandoVinculoRequest r, string acao, string? justificativa,
        Func<VinculoFormacao, DateTime, Task> trabalho, CancellationToken ct)
    {
        await FormacaoBanco.ExecutarAsync(db, logger, r.UsuarioId, r.IpResponsavel, acao, justificativa, async _ =>
        {
            await FormacaoBanco.BloquearAsync(db, Tabela(r.Tipo), r.Id, ct);
            var vinculo = (r.Tipo == TipoVinculo.Classe
                ? await db.MembrosClasses.SingleOrDefaultAsync(v => v.Id == r.Id, ct)
                : (VinculoFormacao?)await db.MembrosEspecialidades.SingleOrDefaultAsync(v => v.Id == r.Id, ct))
                ?? throw NaoEncontrado(r.Tipo);
            await trabalho(vinculo, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            return true;
        }, ConflitoVinculoAtivo(r.Tipo), ct);

        return (await ObterAsync(new ObterVinculoQuery(r.Tipo, r.Id), ct))!;
    }

    private async Task<Usuario> MembroParaNovoVinculoAsync(Guid membroId, CancellationToken ct)
    {
        var membro = await db.Usuarios.AsNoTracking().SingleOrDefaultAsync(u => u.Id == membroId, ct)
            ?? throw ApiProblemException.NotFound("Membro não encontrado.");
        return membro.Ativo ? membro : throw ApiProblemException.Conflict("Membro inativo.", "Somente membros ativos recebem novas associações.");
    }

    private async Task ConferirInstrutorAsync(Guid? instrutorId, CancellationToken ct)
    {
        if (instrutorId is { } id && !await db.Usuarios.AnyAsync(u => u.Id == id && u.Ativo, ct))
        {
            throw ApiProblemException.Invalid("InstrutorId", "instrutorId deve ser um usuário ativo.");
        }
    }

    private DateOnly ConferirDataInicio(DateOnly dataInicio, Usuario membro)
    {
        if (dataInicio > dataCivil.Hoje())
        {
            throw ApiProblemException.Invalid("DataInicio", "dataInicio não pode ser futura.");
        }

        if (membro.DataNascimento is { } nascimento && dataInicio < nascimento)
        {
            throw ApiProblemException.Invalid("DataInicio", "dataInicio não pode ser anterior ao nascimento do membro.");
        }

        return dataInicio;
    }

    private async Task<Especialidade> EspecialidadeAtivaAsync(Guid id, CancellationToken ct)
    {
        var especialidade = await db.Especialidades.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct)
            ?? throw ApiProblemException.NotFound("Especialidade não encontrada.");
        return especialidade.Ativo
            ? especialidade
            : throw ApiProblemException.Conflict("Especialidade inativa.", "Especialidades inativas não recebem novas associações.");
    }

    private async Task<Requisito> RequisitoDoVinculoAsync(VinculoFormacao vinculo, Guid requisitoId, CancellationToken ct) =>
        await db.Requisitos.AsNoTracking().SingleOrDefaultAsync(q => q.Id == requisitoId && q.RevisaoId == vinculo.RevisaoId, ct)
            ?? throw ApiProblemException.NotFound("Requisito não encontrado na revisão usada por este vínculo.");

    private static void ExigirEmAndamento(VinculoFormacao vinculo, string operacao)
    {
        if (vinculo.Situacao != SituacaoVinculo.EmAndamento || vinculo.Historico)
        {
            throw ApiProblemException.Conflict($"Não é possível {operacao}: o vínculo não está em andamento.",
                vinculo.Situacao == SituacaoVinculo.Concluida
                    ? "Reabra o vínculo (com justificativa) antes de alterá-lo."
                    : "Retome o vínculo antes de alterá-lo.");
        }
    }

    private static void LimparConclusao(VinculoFormacao vinculo)
    {
        vinculo.DataConclusao = null;
        vinculo.ConclusaoRegistradaPorUsuarioId = null;
        vinculo.ConclusaoRegistradaEmUtc = null;
        LimparReconhecimento(vinculo);
    }

    private static void LimparReconhecimento(VinculoFormacao vinculo)
    {
        vinculo.DataReconhecimento = null;
        vinculo.ReconhecimentoRegistradoPorUsuarioId = null;
        vinculo.ReconhecimentoRegistradoEmUtc = null;
    }

    private static void Tocar(VinculoFormacao vinculo, Guid usuarioId, DateTime agora)
    {
        vinculo.AtualizadoPorUsuarioId = usuarioId;
        vinculo.AtualizadoEmUtc = agora;
    }

    private static string Tabela(TipoVinculo tipo) => tipo == TipoVinculo.Classe ? "membros_classes" : "membros_especialidades";

    private static string NomeTipo(TipoVinculo tipo) => tipo == TipoVinculo.Classe ? "classe" : "especialidade";

    private static string NomeReconhecimento(TipoVinculo tipo) => tipo == TipoVinculo.Classe ? "investidura" : "entrega";

    private static ApiProblemException NaoEncontrado(TipoVinculo tipo) =>
        ApiProblemException.NotFound(tipo == TipoVinculo.Classe ? "Vínculo de classe não encontrado." : "Vínculo de especialidade não encontrado.");

    private static ApiProblemException ConflitoVinculoAtivo(TipoVinculo tipo) => ApiProblemException.Conflict(
        $"O membro já tem esta {NomeTipo(tipo)} em andamento ou concluída.",
        "Cada membro tem no máximo um vínculo não cancelado por classe/especialidade; retome ou reabra o existente.");
}
