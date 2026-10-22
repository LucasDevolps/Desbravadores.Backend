using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed record EventosLockOptions(int TimeoutMs = 10_000);

public sealed class EventosService(AlmiranteDbContext db, TimeProvider clock, ILogger<EventosService> logger, EventosLockOptions lockOptions,
    SinalAvisosNotificacoes sinal)
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
    /// ser excluído à parte; ele só sai com o participante). Sem recorte de data: eventos passados continuam consultáveis.
    /// </summary>
    public async Task<IReadOnlyList<EventoParticipanteDto>> ListarDoParticipanteAsync(Guid membroId, Guid? eventoId, CancellationToken ct)
    {
        var linhas = await (
                from m in db.EventosMembros.AsNoTracking()
                join e in db.Eventos.AsNoTracking() on m.EventoId equals e.Id
                join l in db.Lancamentos.AsNoTracking() on m.LancamentoId equals l.Id
                where m.MembroId == membroId && m.Ativo && e.Ativo && l.Ativo && (eventoId == null || e.Id == eventoId)
                orderby e.DataEvento, e.Local, e.Id
                select new { Evento = e, l.Valor, l.Vencimento, l.Status })
            .ToListAsync(ct);

        return linhas.Select(x => new EventoParticipanteDto
        {
            Id = x.Evento.Id,
            DataEvento = x.Evento.DataEvento,
            Local = x.Evento.Local,
            Transporte = new TransporteDto(x.Evento.TransporteValor, x.Evento.TransporteEhGratis),
            Alimentacao = new AlimentacaoDto(x.Evento.AlimentacaoIndividual, x.Evento.AlimentacaoValor),
            SeguroObrigatorio = x.Evento.SeguroObrigatorio,
            ValorPorMembro = x.Evento.ValorPorMembro,
            MinhaCobranca = new MinhaCobrancaDto(x.Valor, x.Vencimento, x.Status),
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
            .Select(m => new { m.EventoId, m.MembroId })
            .ToListAsync(ct);
        var lancamentos = await db.Lancamentos.AsNoTracking()
            .Where(l => l.Ativo && l.EventoId != null && ids.Contains(l.EventoId.Value) && l.MembroId != null)
            .Select(l => new { EventoId = l.EventoId!.Value, l.Id, MembroId = l.MembroId!.Value, l.Valor, l.Status })
            .ToListAsync(ct);

        var membrosPorEvento = participantes.ToLookup(p => p.EventoId, p => p.MembroId);
        var lancamentosPorEvento = lancamentos.ToLookup(l => l.EventoId);
        return eventos.Select(e => EventoMapping.ToDto(e,
            membrosPorEvento[e.Id],
            lancamentosPorEvento[e.Id].Select(l => new EventoLancamentoDto(l.Id, l.MembroId, l.Valor, l.Status)))).ToList();
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
                    Local = conteudo.Local,
                    CriadoPorUsuarioId = usuarioId,
                    CriadoEmUtc = agora,
                };
                AplicarConteudo(evento, conteudo);
                db.Eventos.Add(evento);

                var participantes = new List<EventoMembro>(conteudo.Membros.Count);
                var lancamentos = new List<Lancamento>(conteudo.Membros.Count);
                foreach (var membroId in conteudo.Membros)
                {
                    var lancamento = NovoLancamento(evento, membroId, agora);
                    lancamentos.Add(lancamento);
                    participantes.Add(NovoParticipante(evento, membroId, lancamento.Id, agora));
                }

                db.Lancamentos.AddRange(lancamentos);
                db.EventosMembros.AddRange(participantes);
                // Uma notificação por participante deste cadastro, na mesma transação; o lançamento interno não gera outra de débito.
                db.Notificacoes.AddRange(NotificacoesFabrica.ParaEvento(evento, lancamentos, agora));
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
                    lancamentos.Select(l => new EventoLancamentoDto(l.Id, l.MembroId!.Value, l.Valor, l.Status)));

                operacao.RespostaJson = JsonSerializer.Serialize(dto, RespostaJsonOptions);
                await SalvarAsync(ct);

                logger.LogInformation("Evento {EventoId} (grupo {EventoGrupoId}) criado por {UsuarioId} com {Quantidade} lançamentos de {ValorPorMembro}.",
                    eventoId, grupoId, usuarioId, lancamentos.Count, evento.ValorPorMembro);
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

        try
        {
            return await ExecutarAsync(async sessao =>
            {
                var agora = clock.GetUtcNow().UtcDateTime;

                await TravarGrupoDoEventoAsync(id, ct);
                await TravarCadastroAsync(id, versao, request.UsuarioResponsavelId, agora, ct);
                var evento = await db.Eventos.Include(e => e.Membros.Where(m => m.Ativo)).SingleAsync(e => e.Id == id, ct);
                var participantes = evento.Membros.ToList();
                var lancamentos = await db.Lancamentos.Where(l => l.EventoId == id && l.Ativo).ToListAsync(ct);

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
                var financeiroMudou = custoMudou || dataMudou || removidos.Count > 0 || adicionados.Count > 0;

                if (financeiroMudou && lancamentos.Any(l => l.Status != StatusLancamento.Pendente))
                {
                    throw ApiProblemException.Conflict("Evento com lançamento Pago ou Atrasado.",
                        "Valores, participantes e data não podem ser alterados quando algum lançamento do cadastro está Pago ou Atrasado. Apenas o local pode ser corrigido.");
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

                if (removidos.Count > 0 && string.IsNullOrWhiteSpace(request.Motivo))
                {
                    throw ApiProblemException.Invalid(nameof(UpdateEventoRequest.Motivo),
                        "motivo é obrigatório (1–255 caracteres) quando a alteração remove participantes.");
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
                var descricao = EventoRegras.DescricaoLancamento(conteudo.Local, conteudo.DataEvento);
                foreach (var lancamento in lancamentos)
                {
                    if (lancamento.MembroId is { } m && removidosIds.Contains(m))
                    {
                        lancamento.Ativo = false;
                        lancamento.DataAtualizacao = agora;
                        lancamento.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
                        continue;
                    }

                    var pendente = lancamento.Status == StatusLancamento.Pendente;
                    if (lancamento.Descricao != descricao ||
                        (pendente && (lancamento.Valor != conteudo.ValorPorMembro || lancamento.Vencimento != conteudo.DataEvento)))
                    {
                        if (pendente)
                        {
                            lancamento.Valor = conteudo.ValorPorMembro;
                            lancamento.Vencimento = conteudo.DataEvento;
                        }

                        lancamento.Descricao = descricao;
                        lancamento.DataAtualizacao = agora;
                        lancamento.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
                    }
                }

                foreach (var participante in removidos)
                {
                    participante.Ativo = false;
                    participante.DesativadoEmUtc = agora;
                }

                var novosLancamentos = new List<Lancamento>();
                foreach (var membroId in adicionados)
                {
                    var lancamento = NovoLancamento(evento, membroId, agora);
                    novosLancamentos.Add(lancamento);
                    db.Lancamentos.Add(lancamento);
                    db.EventosMembros.Add(NovoParticipante(evento, membroId, lancamento.Id, agora));
                }

                if (removidos.Count > 0)
                {
                    await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, request.Motivo!.Trim(), ct);
                }

                await SalvarAsync(ct);

                logger.LogInformation("Evento {EventoId} atualizado por {UsuarioId}: {Adicionados} adicionados, {Removidos} removidos.",
                    id, request.UsuarioResponsavelId, adicionados.Count, removidos.Count);

                var ativos = lancamentos.Where(l => l.Ativo).Concat(novosLancamentos).ToList();
                return EventoMapping.ToDto(evento,
                    ativos.Select(l => l.MembroId!.Value),
                    ativos.Select(l => new EventoLancamentoDto(l.Id, l.MembroId!.Value, l.Valor, l.Status)));
            }, ct);
        }
        catch (ViolacaoUnicidadeException)
        {
            db.ChangeTracker.Clear();
            var grupoId = await db.Eventos.AsNoTracking().Where(e => e.Id == id).Select(e => e.EventoGrupoId).SingleOrDefaultAsync(ct);
            await ExigirSemConflitoNoGrupoAsync(grupoId, conteudo.Membros, id, ct);
            throw ConflitoInesperado();
        }
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

            if (await db.Lancamentos.AnyAsync(l => l.EventoId == id && l.Ativo && l.Status != StatusLancamento.Pendente, ct))
            {
                throw ApiProblemException.Conflict("Evento com lançamento Pago ou Atrasado.",
                    "O cadastro não pode ser excluído enquanto algum lançamento estiver Pago ou Atrasado. Nenhum registro foi alterado.");
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
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Lancamentos SET Ativo = 0, DataAtualizacao = {agora}, AtualizadoPorUsuarioId = {request.UsuarioResponsavelId} WHERE EventoId = {id} AND Ativo = 1", ct);

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

        var resultado = (int)codigo.Value;
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
        Descricao = EventoRegras.DescricaoLancamento(evento.Local, evento.DataEvento),
        Categoria = CategoriaLancamento.Evento,
        Valor = evento.ValorPorMembro,
        Vencimento = evento.DataEvento,
        Status = StatusLancamento.Pendente,
        TipoFluxo = TipoFluxoLancamento.Entrada,
        DataCriacao = agora,
        Ativo = true,
        EventoId = evento.Id,
    };

    private static EventoMembro NovoParticipante(Evento evento, Guid membroId, Guid lancamentoId, DateTime agora) => new()
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
    public static EventoDto ToDto(Evento e, IEnumerable<Guid> membros, IEnumerable<EventoLancamentoDto> lancamentos)
    {
        var listaMembros = membros.OrderBy(m => m).ToList();
        return new EventoDto
        {
            Id = e.Id,
            EventoGrupoId = e.EventoGrupoId,
            EventoReferenciaId = e.EventoReferenciaId,
            DataEvento = e.DataEvento,
            Local = e.Local,
            Transporte = new TransporteDto(e.TransporteValor, e.TransporteEhGratis),
            Alimentacao = new AlimentacaoDto(e.AlimentacaoIndividual, e.AlimentacaoValor),
            SeguroObrigatorio = e.SeguroObrigatorio,
            Membros = listaMembros,
            QuantidadeMembros = listaMembros.Count,
            ValorPorMembro = e.ValorPorMembro,
            Total = e.ValorPorMembro * listaMembros.Count,
            Ativo = e.Ativo,
            Versao = EventoRegras.VersaoParaTexto(e.Versao),
            Lancamentos = lancamentos.OrderBy(l => l.MembroId).ThenBy(l => l.Id).ToList(),
        };
    }
}
