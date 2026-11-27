using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Resources;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Almirante.Api.Services;

public sealed record EventosLockOptions(int TimeoutMs = 10_000);

public sealed class EventosService(AlmiranteDbContext db, TimeProvider clock, ILogger<EventosService> logger, EventosLockOptions lockOptions,
    SinalAvisosNotificacoes sinal, DataCivil dataCivil, IStringLocalizer<NotificacoesTextos> textos)
{
    public static string RecursoDoGrupo(Guid grupoId) => $"evento-grupo:{grupoId:D}";

    private static readonly JsonSerializerOptions RespostaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public const string CodigoSemRespostaOriginal = "EVENTO_IDEMPOTENCIA_SEM_RESPOSTA_ORIGINAL";

    private const int SqlUnicidadeIndice = 2601;
    private const int SqlUnicidadeConstraint = 2627;

    public async Task<EventosResponse> ListAsync(DateOnly? dataInicial, DateOnly? dataFinal, CancellationToken ct)
    {
        var agora = clock.GetUtcNow();
        var (inicial, final) = dataInicial.HasValue && dataFinal.HasValue
            ? (dataInicial.Value, dataFinal.Value)
            : EventoRegras.PeriodoPadrao(agora);

        var eventos = await db.Eventos.AsNoTracking()
            .Where(e => e.Ativo && e.DataEvento >= inicial && e.DataEvento <= final)
            .OrderBy(e => e.DataEvento).ThenBy(e => e.Local).ThenBy(e => e.Id)
            .ToListAsync(ct);

        return new EventosResponse
        {
            Items = await MontarDtosAsync(eventos, ct),
            DataInicial = inicial,
            DataFinal = final,
            DataMinimaCadastro = EventoRegras.PrimeiroDiaDoMes(agora),
        };
    }

    public async Task<EventoDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var evento = await db.Eventos.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.Ativo, ct);
        return evento is null ? null : (await MontarDtosAsync([evento], ct))[0];
    }

    /// <summary>
    /// Eventos ativos em que <paramref name="membroId"/> é participante ativo, com a própria cobrança (o lançamento do evento não pode
    /// ser excluído à parte; ele só sai com o participante). Evento sem valor a pagar não tem cobrança: o participante o vê com
    /// <c>minhaCobranca</c> nula. As devoluções do membro no evento (issue #209) vêm em <c>minhasDevolucoes</c>. Sem recorte de data:
    /// eventos passados continuam consultáveis.
    /// </summary>
    public async Task<IReadOnlyList<EventoParticipanteDto>> ListarDoParticipanteAsync(Guid membroId, Guid? eventoId, CancellationToken ct)
    {
        var linhas = await (
                from m in db.EventosMembros.AsNoTracking()
                join e in db.Eventos.AsNoTracking() on m.EventoId equals e.Id
                from l in db.Lancamentos.AsNoTracking().Where(l => l.Id == m.LancamentoId && l.Ativo).DefaultIfEmpty()
                where m.MembroId == membroId && m.Ativo && e.Ativo && (eventoId == null || e.Id == eventoId)
                orderby e.DataEvento, e.Local, e.Id
                select new
                {
                    Evento = e,
                    Cobranca = l == null ? null : new MinhaCobrancaDto(l.Valor, l.Vencimento, l.Status),
                })
            .ToListAsync(ct);

        var ids = linhas.Select(x => x.Evento.Id).ToList();
        var devolucoes = ids.Count == 0 ? [] : await db.Lancamentos.AsNoTracking()
            .Where(l => l.Ativo && l.MembroId == membroId && l.LancamentoOrigemId != null && l.EventoId != null && ids.Contains(l.EventoId.Value))
            .OrderBy(l => l.Vencimento).ThenBy(l => l.Id)
            .Select(l => new
            {
                EventoId = l.EventoId!.Value,
                Devolucao = new MinhaDevolucaoDto(l.Valor, l.Vencimento, l.Status, l.ValorPago, l.DataPagamento, l.DevolucaoJustificativa),
            })
            .ToListAsync(ct);
        var devolucoesPorEvento = devolucoes.ToLookup(d => d.EventoId, d => d.Devolucao);

        return linhas.Select(x => new EventoParticipanteDto
        {
            Id = x.Evento.Id,
            Titulo = x.Evento.Titulo,
            Descricao = x.Evento.Descricao,
            DataEvento = x.Evento.DataEvento,
            Local = x.Evento.Local,
            Transporte = new TransporteDto(x.Evento.TransporteValor, x.Evento.TransporteEhGratis),
            Alimentacao = new AlimentacaoDto(x.Evento.AlimentacaoIndividual, x.Evento.AlimentacaoValor),
            SeguroObrigatorio = x.Evento.SeguroObrigatorio,
            ValorPorMembro = x.Evento.ValorPorMembro,
            MinhaCobranca = x.Cobranca,
            MinhasDevolucoes = devolucoesPorEvento[x.Evento.Id].ToList(),
        }).ToList();
    }

    private async Task<IReadOnlyList<EventoDto>> MontarDtosAsync(IReadOnlyList<Evento> eventos, CancellationToken ct)
    {
        if (eventos.Count == 0)
        {
            return [];
        }

        var ids = eventos.Select(e => e.Id).ToList();
        var participantes = await db.EventosMembros.AsNoTracking()
            .Where(m => m.Ativo && ids.Contains(m.EventoId))
            .ToListAsync(ct);
        var lancamentos = await db.Lancamentos.AsNoTracking()
            .Where(l => l.Ativo && l.EventoId != null && ids.Contains(l.EventoId.Value) && l.MembroId != null)
            .ToListAsync(ct);

        var participantesPorEvento = participantes.ToLookup(p => p.EventoId);
        var lancamentosPorEvento = lancamentos.ToLookup(l => l.EventoId!.Value);
        return eventos.Select(e => EventoMapping.ToDto(e,
            participantesPorEvento[e.Id].Select(p => p.MembroId),
            lancamentosPorEvento[e.Id].Where(l => l.LancamentoOrigemId == null).Select(EventoMapping.ToLancamentoDto),
            lancamentosPorEvento[e.Id].Where(l => l.LancamentoOrigemId != null).Select(EventoMapping.ToDevolucaoDto),
            EventoMapping.Isencoes(participantesPorEvento[e.Id]))).ToList();
    }

    public async Task<RegistrarEventoResult> RegistrarAsync(RegistrarEventoRequest request, CancellationToken ct)
    {
        var conteudo = EventoRegras.Normalizar(request);
        var chave = EventoRegras.NormalizarIdempotencyKey(request.IdempotencyKey)!;
        var hash = EventoRegras.Hash(conteudo, request.EventoReferenciaId);
        var usuarioId = request.UsuarioSolicitanteId;

        var jaRegistrada = await LerOperacaoAsync(usuarioId, chave, ct);
        if (jaRegistrada is not null)
        {
            return await ReplayAsync(jaRegistrada, hash, ct);
        }

        ValidarDataDeCadastro(conteudo.DataEvento);

        try
        {
            var criado = await ExecutarAsync(async _ =>
            {
                var grupoId = Guid.NewGuid();
                var eventoId = grupoId;
                if (request.EventoReferenciaId is { } referenciaId)
                {
                    var grupoDaReferencia = await db.Eventos.AsNoTracking().Where(e => e.Id == referenciaId)
                        .Select(e => (Guid?)e.EventoGrupoId).SingleOrDefaultAsync(ct)
                        ?? throw ApiProblemException.NotFound("Evento de referência não encontrado.",
                            "eventoReferenciaId não existe ou está inativo.");
                    await TravarGrupoAsync(grupoDaReferencia, ct);

                    var referencia = await db.Eventos.AsNoTracking()
                        .SingleOrDefaultAsync(e => e.Id == referenciaId && e.Ativo, ct)
                        ?? throw ApiProblemException.NotFound("Evento de referência não encontrado.",
                            "eventoReferenciaId não existe ou está inativo.");
                    ValidarReferencia(referencia, conteudo);
                    grupoId = referencia.EventoGrupoId;
                    eventoId = Guid.NewGuid();
                }

                await ExigirMembrosExistentesAsync(conteudo.Membros, ct);
                if (request.EventoReferenciaId is not null)
                {
                    await ExigirSemConflitoNoGrupoAsync(grupoId, conteudo.Membros, null, ct);
                }

                var agora = clock.GetUtcNow().UtcDateTime;
                var evento = new Evento
                {
                    Id = eventoId,
                    EventoGrupoId = grupoId,
                    EventoReferenciaId = request.EventoReferenciaId,
                    Titulo = conteudo.Titulo,
                    Local = conteudo.Local,
                    CriadoPorUsuarioId = usuarioId,
                    CriadoEmUtc = agora,
                };
                AplicarConteudo(evento, conteudo);
                db.Eventos.Add(evento);

                // Valor por membro zero (ex.: reunião de clube): os membros participam, mas nenhum lançamento é gerado.
                var participantes = new List<EventoMembro>(conteudo.Membros.Count);
                var lancamentos = new List<Lancamento>(conteudo.GeraCobranca ? conteudo.Membros.Count : 0);
                foreach (var membroId in conteudo.Membros)
                {
                    var lancamento = conteudo.GeraCobranca ? NovoLancamento(evento, membroId, agora) : null;
                    if (lancamento is not null)
                    {
                        lancamentos.Add(lancamento);
                    }

                    participantes.Add(NovoParticipante(evento, membroId, lancamento?.Id, agora));
                }

                db.Lancamentos.AddRange(lancamentos);
                db.EventosMembros.AddRange(participantes);
                // Uma notificação por participante deste cadastro, na mesma transação; o lançamento interno não gera outra de débito.
                db.Notificacoes.AddRange(NotificacoesFabrica.ParaEvento(evento, participantes, lancamentos, agora));
                var operacao = new EventoOperacao
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    IdempotencyKey = chave,
                    RequestHash = hash,
                    EventoId = eventoId,
                    CriadoEmUtc = agora,
                };
                db.EventosOperacoes.Add(operacao);

                await SalvarAsync(ct);

                var dto = EventoMapping.ToDto(evento,
                    participantes.Select(p => p.MembroId),
                    lancamentos.Select(EventoMapping.ToLancamentoDto));

                operacao.RespostaJson = JsonSerializer.Serialize(dto, RespostaJsonOptions);
                await SalvarAsync(ct);

                logger.LogInformation("Evento {EventoId} (grupo {EventoGrupoId}) criado por {UsuarioId} com {Participantes} participantes e {Quantidade} lançamentos de {ValorPorMembro}.",
                    eventoId, grupoId, usuarioId, participantes.Count, lancamentos.Count, evento.ValorPorMembro);
                return new RegistrarEventoResult(dto, true);
            }, ct);
            sinal.Sinalizar();
            return criado;
        }
        catch (ViolacaoUnicidadeException)
        {
            db.ChangeTracker.Clear();
            var existente = await LerOperacaoAsync(usuarioId, chave, ct);
            if (existente is not null)
            {
                return await ReplayAsync(existente, hash, ct);
            }

            var grupoId = request.EventoReferenciaId is { } refId
                ? await db.Eventos.AsNoTracking().Where(e => e.Id == refId).Select(e => e.EventoGrupoId).SingleOrDefaultAsync(ct)
                : Guid.Empty;
            await ExigirSemConflitoNoGrupoAsync(grupoId, conteudo.Membros, null, ct);
            throw ConflitoInesperado();
        }
    }

    private Task<EventoOperacao?> LerOperacaoAsync(Guid usuarioId, string chave, CancellationToken ct) =>
        db.EventosOperacoes.AsNoTracking().SingleOrDefaultAsync(o => o.UsuarioId == usuarioId && o.IdempotencyKey == chave, ct);

    private async Task<RegistrarEventoResult> ReplayAsync(EventoOperacao operacao, string hash, CancellationToken ct)
    {
        if (!string.Equals(operacao.RequestHash, hash, StringComparison.Ordinal))
        {
            throw ApiProblemException.Conflict("Chave de idempotência já usada com dados diferentes.",
                "O header Idempotency-Key informado já foi usado em uma operação de evento com dados diferentes.");
        }

        if (!await db.Eventos.AsNoTracking().AnyAsync(e => e.Id == operacao.EventoId && e.Ativo, ct))
        {
            throw ApiProblemException.Conflict("Evento da operação já foi excluído.",
                "Esta operação idempotente já criou um evento que depois foi excluído; use uma nova Idempotency-Key para cadastrar novamente.");
        }

        if (operacao.RespostaJson is null)
        {
            throw ApiProblemException.Conflict("Resposta original da operação indisponível.",
                "Esta operação foi registrada antes de a resposta original passar a ser guardada e o evento já foi criado. " +
                "Consulte o evento existente (GET /api/Eventos/{id}) em vez de reenviar a operação.",
                new Dictionary<string, object?> { ["codigo"] = CodigoSemRespostaOriginal, ["eventoId"] = operacao.EventoId });
        }

        var dto = JsonSerializer.Deserialize<EventoDto>(operacao.RespostaJson, RespostaJsonOptions)
            ?? throw new InvalidOperationException("Resposta original da operação idempotente ilegível.");
        return new RegistrarEventoResult(dto, false);
    }

    private void ValidarDataDeCadastro(DateOnly dataEvento)
    {
        var agora = clock.GetUtcNow();
        if (!EventoRegras.DataDeCadastroValida(dataEvento, agora))
        {
            throw ApiProblemException.Invalid(nameof(RegistrarEventoRequest.DataEvento), EventoRegras.MensagemDataDeCadastro(agora));
        }
    }

    private static void ValidarReferencia(Evento referencia, EventoNormalizado conteudo)
    {
        var erros = new Dictionary<string, string[]>();
        if (referencia.DataEvento != conteudo.DataEvento)
        {
            erros[nameof(RegistrarEventoRequest.DataEvento)] = ["dataEvento deve ser igual à do evento de referência."];
        }

        if (!string.Equals(EventoRegras.NormalizarLocal(referencia.Local), conteudo.Local, StringComparison.OrdinalIgnoreCase))
        {
            erros[nameof(RegistrarEventoRequest.Local)] = ["local deve corresponder ao do evento de referência."];
        }

        if (erros.Count > 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "Dados inválidos.", errors: erros);
        }
    }

    public async Task<EventoDto> UpdateAsync(UpdateEventoRequest request, CancellationToken ct)
    {
        var conteudo = EventoRegras.Normalizar(request);
        EventoRegras.TentarLerVersao(request.Versao, out var versao);
        var id = request.Id;
        var motivo = string.IsNullOrWhiteSpace(request.Motivo) ? null : request.Motivo.Trim();
        var notificou = false;

        try
        {
            var atualizado = await ExecutarAsync(async sessao =>
            {
                notificou = false;
                var agora = clock.GetUtcNow().UtcDateTime;

                await TravarGrupoDoEventoAsync(id, ct);
                await TravarCadastroAsync(id, versao, request.UsuarioResponsavelId, agora, ct);
                var evento = await db.Eventos.Include(e => e.Membros.Where(m => m.Ativo)).SingleAsync(e => e.Id == id, ct);
                var participantes = evento.Membros.ToList();
                var lancamentos = await db.Lancamentos.Where(l => l.EventoId == id && l.Ativo).ToListAsync(ct);
                var cobrancas = lancamentos.Where(l => l.LancamentoOrigemId == null).ToList();
                var devolucoes = lancamentos.Where(l => l.LancamentoOrigemId != null).ToList();
                var pagos = cobrancas.Where(l => l.Status == StatusLancamento.Pago).ToList();

                var atuais = participantes.Select(p => p.MembroId).ToHashSet();
                var novos = conteudo.Membros.ToHashSet();
                var removidos = participantes.Where(p => !novos.Contains(p.MembroId)).ToList();
                var adicionados = conteudo.Membros.Where(m => !atuais.Contains(m)).ToList();

                var dataMudou = conteudo.DataEvento != evento.DataEvento;
                var localMudou = !string.Equals(conteudo.Local, evento.Local, StringComparison.Ordinal);
                var custoMudou = conteudo.TransporteEhGratis != evento.TransporteEhGratis
                    || conteudo.TransporteValor != evento.TransporteValor
                    || conteudo.AlimentacaoIndividual != evento.AlimentacaoIndividual
                    || conteudo.AlimentacaoValor != evento.AlimentacaoValor
                    || conteudo.SeguroObrigatorio != evento.SeguroObrigatorio
                    || conteudo.ValorPorMembro != evento.ValorPorMembro;
                var reducao = conteudo.ValorPorMembro < evento.ValorPorMembro;
                var hoje = dataCivil.Hoje();

                // Issue #184: Atrasado é só um Pendente vencido (a rotina de vencimento o marca sozinha); apenas Pago trava o cadastro.
                // Issue #209: com Pago, o valor por membro ainda pode diminuir (quem pagou a mais recebe devolução) e participantes podem
                // sair conforme ExigirRemocaoDePagantesPermitida; aumento, outra composição com o mesmo total, inclusão e data seguem travados.
                if (pagos.Count > 0)
                {
                    if ((custoMudou && !reducao) || dataMudou || adicionados.Count > 0)
                    {
                        throw ApiProblemException.Conflict("Evento com lançamento Pago.",
                            "Com lançamento Pago, o valor por membro só pode diminuir (aumento ou outra composição com o mesmo total retorna 409), " +
                            "participantes não podem ser incluídos e a data não muda. Local, título e descrição podem ser corrigidos.");
                    }

                    ExigirRemocaoDePagantesPermitida(evento, removidos, pagos, devolucoes, hoje);
                }

                if (dataMudou && conteudo.DataEvento < EventoRegras.PrimeiroDiaDoMes(clock.GetUtcNow()))
                {
                    throw ApiProblemException.Invalid(nameof(UpdateEventoRequest.DataEvento),
                        $"dataEvento alterada deve ser a partir de {EventoRegras.PrimeiroDiaDoMes(clock.GetUtcNow()):yyyy-MM-dd} (primeiro dia do mês atual, UTC).");
                }

                if ((dataMudou || localMudou) &&
                    await db.Eventos.AnyAsync(e => e.EventoGrupoId == evento.EventoGrupoId && e.Id != id && e.Ativo, ct))
                {
                    throw ApiProblemException.Conflict("Data e local são compartilhados com outros cadastros do passeio.",
                        "Existe mais de um cadastro ativo neste passeio; data e local não podem ser alterados por um PUT isolado.");
                }

                var aDevolver = reducao ? DevolucoesPorReducao(pagos, devolucoes, conteudo.ValorPorMembro) : [];

                // Valor por membro que passa a zero cancela as cobranças em aberto; as Pagas ficam (histórico do caixa) e geram devolução.
                var cancelaCobrancas = EventoRegras.GeraCobranca(evento.ValorPorMembro) && !conteudo.GeraCobranca
                    && cobrancas.Any(l => l.Status != StatusLancamento.Pago);

                if ((removidos.Count > 0 || cancelaCobrancas || aDevolver.Count > 0) && motivo is null)
                {
                    throw ApiProblemException.Invalid(nameof(UpdateEventoRequest.Motivo),
                        "motivo é obrigatório (1–255 caracteres) quando a alteração remove participantes, zera o valor de um evento com cobranças ou gera devoluções.");
                }

                if (adicionados.Count > 0)
                {
                    await ExigirMembrosExistentesAsync(adicionados, ct);
                    await ExigirSemConflitoNoGrupoAsync(evento.EventoGrupoId, adicionados, id, ct);
                }

                AplicarConteudo(evento, conteudo);
                evento.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
                evento.AtualizadoEmUtc = agora;

                var removidosIds = removidos.Select(r => r.MembroId).ToHashSet();
                var descricao = EventoRegras.DescricaoLancamento(conteudo.Titulo, conteudo.DataEvento);
                foreach (var lancamento in cobrancas)
                {
                    var emAberto = lancamento.Status != StatusLancamento.Pago;
                    if (emAberto && (cancelaCobrancas || (lancamento.MembroId is { } m && removidosIds.Contains(m))))
                    {
                        lancamento.Ativo = false;
                        lancamento.DataAtualizacao = agora;
                        lancamento.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
                        continue;
                    }

                    var valorDoEvento = lancamento.ValorAjustado ? lancamento.Valor : conteudo.ValorPorMembro;
                    if (lancamento.Descricao != descricao ||
                        (emAberto && (lancamento.Valor != valorDoEvento || lancamento.Vencimento != conteudo.DataEvento)))
                    {
                        if (emAberto)
                        {
                            lancamento.Valor = valorDoEvento;
                            lancamento.Vencimento = conteudo.DataEvento;
                            // Vencimento que ainda não passou tira do atraso; um vencido fica para a rotina, que marca e notifica.
                            if (lancamento.Status == StatusLancamento.Atrasado && lancamento.Vencimento >= hoje)
                            {
                                lancamento.Status = StatusLancamento.Pendente;
                            }
                        }

                        lancamento.Descricao = descricao;
                        lancamento.DataAtualizacao = agora;
                        lancamento.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
                    }
                }

                var descricaoDevolucao = DevolucaoRegras.Descricao(conteudo.Titulo, conteudo.DataEvento);
                foreach (var devolucao in devolucoes.Where(d => d.Descricao != descricaoDevolucao))
                {
                    devolucao.Descricao = descricaoDevolucao;
                    devolucao.DataAtualizacao = agora;
                    devolucao.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
                }

                foreach (var participante in removidos)
                {
                    participante.Ativo = false;
                    participante.DesativadoEmUtc = agora;
                }

                var mantidos = participantes.Where(p => p.Ativo).ToList();
                var novosLancamentos = new List<Lancamento>();
                if (cancelaCobrancas)
                {
                    var desativadas = cobrancas.Where(l => !l.Ativo).Select(l => l.Id).ToHashSet();
                    foreach (var participante in mantidos.Where(p => p.LancamentoId is { } l && desativadas.Contains(l)))
                    {
                        participante.LancamentoId = null;
                    }
                }
                else if (conteudo.GeraCobranca)
                {
                    // Evento que passa a ter valor: quem participava sem cobrança ganha o lançamento pendente agora.
                    var comCobranca = cobrancas.Where(l => l.Ativo && l.MembroId is not null).Select(l => l.MembroId!.Value).ToHashSet();
                    foreach (var participante in mantidos.Where(p => !p.Isento && !comCobranca.Contains(p.MembroId)))
                    {
                        var lancamento = NovoLancamento(evento, participante.MembroId, agora);
                        novosLancamentos.Add(lancamento);
                        db.Lancamentos.Add(lancamento);
                        participante.LancamentoId = lancamento.Id;
                    }
                }

                foreach (var membroId in adicionados)
                {
                    var lancamento = conteudo.GeraCobranca ? NovoLancamento(evento, membroId, agora) : null;
                    if (lancamento is not null)
                    {
                        novosLancamentos.Add(lancamento);
                        db.Lancamentos.Add(lancamento);
                    }

                    db.EventosMembros.Add(NovoParticipante(evento, membroId, lancamento?.Id, agora));
                }

                var novasDevolucoes = aDevolver
                    .Select(d => DevolucaoRegras.Nova(d.Origem, d.Valor, evento.Titulo, evento.DataEvento, hoje, motivo!, agora))
                    .ToList();
                AdicionarDevolucoes(novasDevolucoes, evento.Titulo, agora);

                if (removidos.Count > 0 || cancelaCobrancas)
                {
                    await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, motivo!, ct);
                }

                await SalvarAsync(ct);
                notificou = novasDevolucoes.Count > 0;

                logger.LogInformation("Evento {EventoId} atualizado por {UsuarioId}: {Adicionados} adicionados, {Removidos} removidos, " +
                    "{LancamentosCriados} lançamentos criados, cobranças canceladas: {Canceladas}, {Devolucoes} devoluções geradas.",
                    id, request.UsuarioResponsavelId, adicionados.Count, removidos.Count, novosLancamentos.Count, cancelaCobrancas, novasDevolucoes.Count);

                var ativos = cobrancas.Where(l => l.Ativo).Concat(novosLancamentos).ToList();
                return EventoMapping.ToDto(evento,
                    mantidos.Select(p => p.MembroId).Concat(adicionados),
                    ativos.Select(EventoMapping.ToLancamentoDto),
                    devolucoes.Concat(novasDevolucoes).Select(EventoMapping.ToDevolucaoDto),
                    EventoMapping.Isencoes(mantidos));
            }, ct);

            if (notificou)
            {
                sinal.Sinalizar();
            }

            return atualizado;
        }
        catch (ViolacaoUnicidadeException)
        {
            db.ChangeTracker.Clear();
            var grupoId = await db.Eventos.AsNoTracking().Where(e => e.Id == id).Select(e => e.EventoGrupoId).SingleOrDefaultAsync(ct);
            await ExigirSemConflitoNoGrupoAsync(grupoId, conteudo.Membros, id, ct);
            throw ConflitoInesperado();
        }
    }

    /// <summary>
    /// Issue #209: participante que pagou só sai antes da data do evento depois do estorno concluído (devolução gerada e baixada); no dia
    /// do evento ou depois, sai sem devolução. O lançamento Pago dele continua ativo nos dois casos.
    /// </summary>
    private static void ExigirRemocaoDePagantesPermitida(Evento evento, IReadOnlyCollection<EventoMembro> removidos,
        IReadOnlyCollection<Lancamento> pagos, IReadOnlyCollection<Lancamento> devolucoes, DateOnly hoje)
    {
        if (removidos.Count == 0 || evento.DataEvento <= hoje)
        {
            return;
        }

        var removidosIds = removidos.Select(r => r.MembroId).ToHashSet();
        var bloqueados = pagos
            .Where(p => p.MembroId is { } m && removidosIds.Contains(m)
                && !DevolucaoRegras.EstornoConcluido(p, devolucoes.Where(d => d.LancamentoOrigemId == p.Id).ToList()))
            .Select(p => p.MembroId!.Value)
            .Distinct()
            .Order()
            .ToList();
        if (bloqueados.Count > 0)
        {
            throw ApiProblemException.Conflict("Participante com pagamento.",
                "Antes da data do evento, quem já pagou só pode ser removido depois do estorno concluído: gere a devolução em " +
                "POST /api/Eventos/{id}/Participantes/{membroId}/Estorno e dê baixa nela em POST /api/Lancamentos/{id}/Devolucao. " +
                "Membros: " + string.Join(", ", bloqueados) + ".",
                new Dictionary<string, object?> { ["membrosComPagamento"] = bloqueados });
        }
    }

    /// <summary>Uma devolução por pagamento cujo valor pago fica acima do novo valor por membro (descontado o que já foi devolvido).</summary>
    private static List<(Lancamento Origem, decimal Valor)> DevolucoesPorReducao(IEnumerable<Lancamento> pagos,
        IReadOnlyCollection<Lancamento> devolucoes, decimal novoValorPorMembro)
    {
        var resultado = new List<(Lancamento Origem, decimal Valor)>();
        foreach (var pago in pagos.Where(p => !p.ValorAjustado).OrderBy(p => p.MembroId).ThenBy(p => p.Id))
        {
            var valor = DevolucaoRegras.PorReducao(ValorPagoConhecido(pago), novoValorPorMembro,
                devolucoes.Where(d => d.LancamentoOrigemId == pago.Id).Sum(d => d.Valor));
            if (valor > 0)
            {
                resultado.Add((pago, valor));
            }
        }

        return resultado;
    }

    /// <summary>"Pago" histórico sem dados de liquidação não diz quanto entrou: a devolução exige regularizar o pagamento antes.</summary>
    private static decimal ValorPagoConhecido(Lancamento pago) => pago.ValorPago ?? throw ApiProblemException.Conflict(
        "Pagamento sem dados de liquidação.",
        "Há lançamento Pago sem data e valor de pagamento registrados (\"Pago\" histórico). Registre o pagamento em " +
        "POST /api/Lancamentos/{id}/Pagamento antes de gerar devoluções.",
        new Dictionary<string, object?> { ["lancamentoId"] = pago.Id });

    private void AdicionarDevolucoes(IReadOnlyCollection<Lancamento> devolucoes, string tituloEvento, DateTime agora)
    {
        db.Lancamentos.AddRange(devolucoes);
        db.Notificacoes.AddRange(devolucoes.Select(d => NotificacoesFabrica.DevolucaoEvento(textos, d, tituloEvento, agora)));
    }

    /// <summary>
    /// Issue #209: gera, para o membro, a devolução de tudo o que ele ainda tem retido nos pagamentos do evento (valor pago menos as
    /// devoluções já geradas). Vale para participante ativo ou já removido que ainda tenha pagamento ativo no cadastro.
    /// </summary>
    public async Task<EventoDto> EstornarParticipanteAsync(EstornarParticipanteRequest request, CancellationToken ct)
    {
        EventoRegras.TentarLerVersao(request.Versao, out var versao);
        var id = request.Id;
        var membroId = request.MembroId;
        var motivo = request.Motivo!.Trim();

        var dto = await ExecutarAsync(async _ =>
        {
            var agora = clock.GetUtcNow().UtcDateTime;
            await TravarGrupoDoEventoAsync(id, ct);
            await TravarCadastroAsync(id, versao, request.UsuarioResponsavelId, agora, ct);

            var evento = await db.Eventos.AsNoTracking().SingleAsync(e => e.Id == id, ct);
            var lancamentos = await db.Lancamentos.Where(l => l.EventoId == id && l.Ativo && l.MembroId == membroId).ToListAsync(ct);
            if (lancamentos.Count == 0 &&
                !await db.EventosMembros.AsNoTracking().AnyAsync(m => m.EventoId == id && m.MembroId == membroId && m.Ativo, ct))
            {
                throw ApiProblemException.NotFound("Participante não encontrado.",
                    "O membro não participa deste evento e não tem lançamento ativo nele.");
            }

            var devolucoes = lancamentos.Where(l => l.LancamentoOrigemId != null).ToList();
            var hoje = dataCivil.Hoje();
            var novas = new List<Lancamento>();
            foreach (var pago in lancamentos.Where(l => l.LancamentoOrigemId == null && l.Status == StatusLancamento.Pago).OrderBy(l => l.Id))
            {
                var retido = DevolucaoRegras.Retido(ValorPagoConhecido(pago),
                    devolucoes.Where(d => d.LancamentoOrigemId == pago.Id).Sum(d => d.Valor));
                if (retido > 0)
                {
                    novas.Add(DevolucaoRegras.Nova(pago, retido, evento.Titulo, evento.DataEvento, hoje, motivo, agora));
                }
            }

            if (novas.Count == 0)
            {
                throw ApiProblemException.Conflict("Nada a devolver.",
                    "O membro não tem pagamento registrado neste evento, ou todo o valor pago já foi devolvido ou está em devolução.");
            }

            AdicionarDevolucoes(novas, evento.Titulo, agora);
            await SalvarAsync(ct);

            logger.LogInformation("Estorno de {MembroId} no evento {EventoId} por {UsuarioId}: {Devolucoes} devoluções, total {Total}.",
                membroId, id, request.UsuarioResponsavelId, novas.Count, novas.Sum(d => d.Valor));
            return (await MontarDtosAsync([evento], ct))[0];
        }, ct);

        sinal.Sinalizar();
        return dto;
    }

    public async Task DeleteAsync(DeleteEventoRequest request, CancellationToken ct)
    {
        EventoRegras.TentarLerVersao(request.Versao, out var versao);
        var id = request.Id;
        var motivo = request.Motivo!.Trim();

        await ExecutarAsync<bool>(async sessao =>
        {
            var agora = clock.GetUtcNow().UtcDateTime;
            await TravarGrupoDoEventoAsync(id, ct);
            await TravarCadastroAsync(id, versao, request.UsuarioResponsavelId, agora, ct);

            // Issue #209: a exclusão só passa quando nenhum pagamento ficou retido (todo valor pago devolvido e baixado).
            var pagos = await db.Lancamentos.AsNoTracking()
                .Where(l => l.EventoId == id && l.Ativo && l.LancamentoOrigemId == null && l.Status == StatusLancamento.Pago).ToListAsync(ct);
            var devolucoes = await db.Lancamentos.AsNoTracking()
                .Where(l => l.EventoId == id && l.Ativo && l.LancamentoOrigemId != null).ToListAsync(ct);
            if (devolucoes.Any(d => d.Status != StatusLancamento.Pago) ||
                pagos.Any(p => DevolucaoRegras.TemValorRetido(p, devolucoes.Where(d => d.LancamentoOrigemId == p.Id))))
            {
                throw ApiProblemException.Conflict("Evento com valor retido.",
                    "O cadastro não pode ser excluído enquanto algum pagamento não tiver sido totalmente estornado (devoluções geradas e baixadas). " +
                    "Nenhum registro foi alterado.");
            }

            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, motivo, ct);

            var eventos = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.eventos SET Ativo = 0 WHERE Id = {id} AND Ativo = 1", ct);
            if (eventos != 1)
            {
                throw new InvalidOperationException("Evento travado não pôde ser desativado.");
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.evento_membros SET Ativo = 0, DesativadoEmUtc = {agora} WHERE EventoId = {id} AND Ativo = 1", ct);
            // Pagamentos e devoluções baixadas ficam ativos: são o histórico do caixa (entrada e saída que de fato aconteceram).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Lancamentos SET Ativo = 0, DataAtualizacao = {agora}, AtualizadoPorUsuarioId = {request.UsuarioResponsavelId} WHERE EventoId = {id} AND Ativo = 1 AND Status <> {(int)StatusLancamento.Pago}", ct);

            logger.LogInformation("Evento {EventoId} excluído logicamente por {UsuarioId}.", id, request.UsuarioResponsavelId);
            return true;
        }, ct);
    }

    private async Task TravarGrupoAsync(Guid grupoId, CancellationToken ct)
    {
        var codigo = new SqlParameter("@codigo", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "EXEC @codigo = sys.sp_getapplock @Resource = @recurso, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @espera;",
            [codigo, new SqlParameter("@recurso", RecursoDoGrupo(grupoId)), new SqlParameter("@espera", lockOptions.TimeoutMs)], ct);

        ConferirLockDoGrupo((int)codigo.Value, grupoId, logger);
    }

    /// <summary>
    /// Retorno do sp_getapplock: 0/1 concedido; -999 é erro de parâmetro (falha da aplicação, não do usuário); os demais negativos
    /// (tempo esgotado, cancelado, deadlock) são lock não concedido e viram 409.
    /// </summary>
    internal static void ConferirLockDoGrupo(int resultado, Guid grupoId, ILogger logger)
    {
        if (resultado >= 0)
        {
            return;
        }

        if (resultado == -999)
        {
            throw new InvalidOperationException("sp_getapplock recusou os parâmetros do lock do passeio.");
        }

        logger.LogWarning("Lock do passeio {EventoGrupoId} não adquirido (sp_getapplock {Resultado}).", grupoId, resultado);
        throw ApiProblemException.Conflict("Passeio em uso por outra operação.",
            "Outra operação está alterando este passeio agora. Nada foi alterado; tente novamente em instantes.");
    }

    private async Task TravarGrupoDoEventoAsync(Guid eventoId, CancellationToken ct)
    {
        var grupoId = await db.Eventos.AsNoTracking().Where(e => e.Id == eventoId).Select(e => (Guid?)e.EventoGrupoId).SingleOrDefaultAsync(ct)
            ?? throw ApiProblemException.NotFound("Evento não encontrado.");
        await TravarGrupoAsync(grupoId, ct);
    }

    private async Task TravarCadastroAsync(Guid id, byte[] versao, Guid usuarioId, DateTime agora, CancellationToken ct)
    {
        var linhas = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.eventos SET AtualizadoEmUtc = {agora}, AtualizadoPorUsuarioId = {usuarioId} WHERE Id = {id} AND Ativo = 1 AND Versao = {versao}", ct);
        if (linhas == 1)
        {
            return;
        }

        var ativo = await db.Eventos.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.Ativo).SingleOrDefaultAsync(ct);
        if (ativo != true)
        {
            throw ApiProblemException.NotFound("Evento não encontrado.");
        }

        throw ApiProblemException.Conflict("Versão do evento desatualizada.",
            "O cadastro foi alterado por outra operação (inclusive mudança de status de um lançamento). Consulte novamente e reenvie com a nova versao.");
    }

    private async Task ExigirMembrosExistentesAsync(IReadOnlyCollection<Guid> membros, CancellationToken ct)
    {
        var existentes = await db.Usuarios.FromSqlRaw("SELECT * FROM dbo.Usuarios WITH (REPEATABLEREAD)").AsNoTracking()
            .Where(u => u.Ativo && membros.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        var faltantes = membros.Except(existentes).OrderBy(m => m).ToList();
        if (faltantes.Count > 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "Dados inválidos.",
                errors: new Dictionary<string, string[]> { ["Membros"] = ["membros inexistentes: " + string.Join(", ", faltantes) + "."] },
                extensions: new Dictionary<string, object?> { ["membrosInexistentes"] = faltantes });
        }
    }

    private async Task ExigirSemConflitoNoGrupoAsync(Guid grupoId, IReadOnlyCollection<Guid> membros, Guid? ignorarEventoId, CancellationToken ct)
    {
        if (grupoId == Guid.Empty)
        {
            return;
        }

        var conflitos = await db.EventosMembros.AsNoTracking()
            .Where(m => m.Ativo && m.EventoGrupoId == grupoId && membros.Contains(m.MembroId)
                && (ignorarEventoId == null || m.EventoId != ignorarEventoId))
            .Select(m => m.MembroId).Distinct().ToListAsync(ct);
        if (conflitos.Count > 0)
        {
            conflitos.Sort();
            throw ApiProblemException.Conflict("Membro já participa de outro cadastro ativo deste passeio.",
                "Cada membro só pode pertencer a um grupo ativo por passeio: " + string.Join(", ", conflitos) + ".",
                new Dictionary<string, object?> { ["membrosConflitantes"] = conflitos });
        }
    }

    private static void AplicarConteudo(Evento evento, EventoNormalizado conteudo)
    {
        evento.Titulo = conteudo.Titulo;
        evento.Descricao = conteudo.Descricao;
        evento.DataEvento = conteudo.DataEvento;
        evento.Local = conteudo.Local;
        evento.TransporteEhGratis = conteudo.TransporteEhGratis;
        evento.TransporteValor = conteudo.TransporteValor;
        evento.AlimentacaoIndividual = conteudo.AlimentacaoIndividual;
        evento.AlimentacaoValor = conteudo.AlimentacaoValor;
        evento.SeguroObrigatorio = conteudo.SeguroObrigatorio;
        evento.ValorPorMembro = conteudo.ValorPorMembro;
    }

    private static Lancamento NovoLancamento(Evento evento, Guid membroId, DateTime agora) => new()
    {
        Id = Guid.NewGuid(),
        MembroId = membroId,
        Finalidade = null,
        Descricao = EventoRegras.DescricaoLancamento(evento.Titulo, evento.DataEvento),
        Categoria = CategoriaLancamento.Evento,
        Valor = evento.ValorPorMembro,
        Vencimento = evento.DataEvento,
        Status = StatusLancamento.Pendente,
        TipoFluxo = TipoFluxoLancamento.Entrada,
        DataCriacao = agora,
        Ativo = true,
        EventoId = evento.Id,
    };

    private static EventoMembro NovoParticipante(Evento evento, Guid membroId, Guid? lancamentoId, DateTime agora) => new()
    {
        Id = Guid.NewGuid(),
        EventoId = evento.Id,
        EventoGrupoId = evento.EventoGrupoId,
        MembroId = membroId,
        LancamentoId = lancamentoId,
        Ativo = true,
        CriadoEmUtc = agora,
    };

    private async Task SalvarAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiProblemException.Conflict("Versão do evento desatualizada.",
                "O cadastro foi alterado por outra operação. Consulte novamente e reenvie com a nova versao.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: SqlUnicidadeIndice or SqlUnicidadeConstraint })
        {
            throw new ViolacaoUnicidadeException();
        }
    }

    private Task<T> ExecutarAsync<T>(Func<SessaoAuditoria, Task<T>> trabalho, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            throw new NotSupportedException("Eventos exigem SQL Server (transação, rowversion, trigger e SESSION_CONTEXT).");
        }

        return SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, trabalho, ct);
    }

    private static ApiProblemException ConflitoInesperado() => ApiProblemException.Conflict("Conflito de concorrência.",
        "Outra operação alterou os mesmos dados simultaneamente. Consulte novamente e tente de novo.");

    private sealed class ViolacaoUnicidadeException : Exception;

}

internal static class EventoMapping
{
    public static EventoLancamentoDto ToLancamentoDto(Lancamento l) =>
        new(l.Id, l.MembroId!.Value, l.Valor, l.Status, l.ValorPago, l.ValorAjustado);

    public static IEnumerable<EventoIsencaoDto> Isencoes(IEnumerable<EventoMembro> participantes) => participantes
        .Where(p => p.Isento)
        .Select(p => new EventoIsencaoDto(p.MembroId, p.IsencaoLancamentoId!.Value, p.IsencaoMotivo!, p.IsentoPorUsuarioId!.Value,
            p.IsentoEmUtc!.Value));

    public static EventoDevolucaoDto ToDevolucaoDto(Lancamento d) => new(d.Id, d.MembroId!.Value, d.LancamentoOrigemId!.Value, d.Valor,
        d.Vencimento, d.Status, d.ValorPago, d.DataPagamento, d.DevolucaoMotivo, d.DevolucaoJustificativa);

    public static EventoDto ToDto(Evento e, IEnumerable<Guid> membros, IEnumerable<EventoLancamentoDto> lancamentos,
        IEnumerable<EventoDevolucaoDto>? devolucoes = null, IEnumerable<EventoIsencaoDto>? isencoes = null)
    {
        var listaMembros = membros.OrderBy(m => m).ToList();
        var listaLancamentos = lancamentos.OrderBy(l => l.MembroId).ThenBy(l => l.Id).ToList();
        var listaDevolucoes = (devolucoes ?? []).OrderBy(d => d.MembroId).ThenBy(d => d.Vencimento).ThenBy(d => d.Id).ToList();
        var totais = EventoRegras.Totais(e.ValorPorMembro, listaLancamentos, listaDevolucoes);
        return new EventoDto
        {
            Id = e.Id,
            EventoGrupoId = e.EventoGrupoId,
            EventoReferenciaId = e.EventoReferenciaId,
            Titulo = e.Titulo,
            Descricao = e.Descricao,
            DataEvento = e.DataEvento,
            Local = e.Local,
            Transporte = new TransporteDto(e.TransporteValor, e.TransporteEhGratis),
            Alimentacao = new AlimentacaoDto(e.AlimentacaoIndividual, e.AlimentacaoValor),
            SeguroObrigatorio = e.SeguroObrigatorio,
            Membros = listaMembros,
            QuantidadeMembros = listaMembros.Count,
            ValorPorMembro = e.ValorPorMembro,
            Total = totais.Estimado,
            TotalEstimado = totais.Estimado,
            TotalPago = totais.Pago,
            ValorAReceber = totais.AReceber,
            TotalDevolvido = totais.Devolvido,
            Ativo = e.Ativo,
            Versao = EventoRegras.VersaoParaTexto(e.Versao),
            Lancamentos = listaLancamentos,
            Devolucoes = listaDevolucoes,
            Isencoes = (isencoes ?? []).OrderBy(i => i.MembroId).ToList(),
        };
    }
}
