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

// Regras de negócio de /api/Eventos. Toda escrita acontece em UMA transação SQL (evento, participantes,
// lançamentos, idempotência), com um único SaveChanges em lote (sem N+1) e sem chamar /Lancamentos por HTTP.
//
// Ordem ÚNICA de locks (evita deadlock entre POST, PUT, DELETE e a mudança de status de lançamentos, ver
// LancamentosService.UpdateAsync). Cada fluxo adquire só um subconjunto, sempre nesta ordem:
//   1) coordenador do passeio: sp_getapplock exclusivo, dono = transação, recurso "evento-grupo:{EventoGrupoId}"
//      (POST com eventoReferenciaId, PUT e DELETE). EventoGrupoId é imutável e sobrevive à exclusão do primeiro
//      cadastro, então identifica o passeio para todos os cadastros de preço, em qualquer instância da API;
//   2) UPDATE em eventos filtrado por Id/Ativo/Versao (valida a rowversion e trava a linha do cadastro);
//   3) leitura/alteração de evento_membros e Lancamentos.
// O pagamento (LancamentosService) usa (2) e (3): não precisa do coordenador, pois só altera o status. Nenhum fluxo
// pega um lock de posição menor depois de um de posição maior. A espera pelo coordenador é limitada
// (EventosLockOptions) e o lock é liberado no commit/rollback; nenhuma linha de outro cadastro é tocada só para
// coordenar (a rowversion dos demais cadastros não muda).
// Quem altera o status de um lançamento de evento também toca a linha do evento ANTES, então a verificação
// "há lançamento Pago/Atrasado?" feita aqui, com o evento travado, não pode ser invalidada até o commit.

// Espera máxima pelo lock de coordenação do passeio (sp_getapplock); depois disso a requisição desiste com 409.
public sealed record EventosLockOptions(int TimeoutMs = 10_000);

public sealed class EventosService(AlmiranteDbContext db, TimeProvider clock, ILogger<EventosService> logger, EventosLockOptions lockOptions)
{
    public static string RecursoDoGrupo(Guid grupoId) => $"evento-grupo:{grupoId:D}";

    // Resposta original do POST: JSON camelCase (mesmo contrato da API) com enums por nome.
    private static readonly JsonSerializerOptions RespostaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public const string CodigoSemRespostaOriginal = "EVENTO_IDEMPOTENCIA_SEM_RESPOSTA_ORIGINAL";

    private const int SqlUnicidadeIndice = 2601;
    private const int SqlUnicidadeConstraint = 2627;

    // ---------------------------------------------------------------- consultas

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

    // Duas consultas em lote para a página inteira (participantes e lançamentos), nunca uma por evento.
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

    // ---------------------------------------------------------------- POST

    public async Task<RegistrarEventoResult> RegistrarAsync(RegistrarEventoRequest request, CancellationToken ct)
    {
        // Validação e normalização já ocorreram (ValidationBehavior); "membros" é uma lista, seja qual for o JSON.
        var conteudo = EventoRegras.Normalizar(request);
        var chave = EventoRegras.NormalizarIdempotencyKey(request.IdempotencyKey)!;
        var hash = EventoRegras.Hash(conteudo, request.EventoReferenciaId);
        var usuarioId = request.UsuarioSolicitanteId;

        // Replay ANTES das regras exclusivas de criação que dependem do relógio: um reenvio legítimo não pode falhar
        // só porque o mês virou. O hash cobre o conteúdo normalizado inteiro, então uma chave existente não autoriza
        // nada (a autenticação, a autorização e a leitura do corpo já aconteceram antes, no pipeline).
        var jaRegistrada = await LerOperacaoAsync(usuarioId, chave, ct);
        if (jaRegistrada is not null)
        {
            return await ReplayAsync(jaRegistrada, hash, ct);
        }

        ValidarDataDeCadastro(conteudo.DataEvento);

        try
        {
            return await ExecutarAsync(async _ =>
            {
                var grupoId = Guid.NewGuid();
                var eventoId = grupoId;
                if (request.EventoReferenciaId is { } referenciaId)
                {
                    // O EventoGrupoId da referência é imutável (existe mesmo que ela esteja inativa), então pode ser lido
                    // antes do lock. Depois de adquirir o coordenador do passeio, a referência é relida e validada:
                    // um PUT concorrente que mudou data/local já confirmou (ou ainda vai esperar por este POST).
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

                // Gravação 1: evento, participantes, lançamentos e a operação (a chave única já barra o duplicado aqui).
                // A rowversion do cadastro só existe depois do INSERT (o banco a gera).
                await SalvarAsync(ct);

                var dto = EventoMapping.ToDto(evento,
                    participantes.Select(p => p.MembroId),
                    lancamentos.Select(l => new EventoLancamentoDto(l.Id, l.MembroId!.Value, l.Valor, l.Status)));

                // Gravação 2, MESMA transação: a resposta original (com a versão original) fica imutável junto do
                // evento. Uma falha aqui reverte tudo; o commit só acontece depois, em ExecutarAsync.
                operacao.RespostaJson = JsonSerializer.Serialize(dto, RespostaJsonOptions);
                await SalvarAsync(ct);

                logger.LogInformation("Evento {EventoId} (grupo {EventoGrupoId}) criado por {UsuarioId} com {Quantidade} lançamentos de {ValorPorMembro}.",
                    eventoId, grupoId, usuarioId, lancamentos.Count, evento.ValorPorMembro);
                return new RegistrarEventoResult(dto, true);
            }, ct);
        }
        catch (ViolacaoUnicidadeException)
        {
            // Concorrência: outra requisição gravou primeiro a mesma chave (replay) ou o mesmo participante
            // no mesmo grupo (409). O estado já foi revertido; resolve lendo o que venceu.
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

    // A chave pertence ao par (usuário autenticado, Idempotency-Key): nunca lê a operação de outro usuário.
    private Task<EventoOperacao?> LerOperacaoAsync(Guid usuarioId, string chave, CancellationToken ct) =>
        db.EventosOperacoes.AsNoTracking().SingleOrDefaultAsync(o => o.UsuarioId == usuarioId && o.IdempotencyKey == chave, ct);

    // Mesma chave + mesma operação lógica: devolve o resultado ORIGINAL do POST (200), com a versão original — o GET é
    // que mostra o estado atual, e o PUT com a versão original continua recebendo 409 depois de qualquer alteração.
    // Hash diferente: 409. Cadastro excluído depois: 409 sem recriar, reativar nem duplicar histórico. Operação anterior
    // à coluna RespostaJson (sem fonte confiável do resultado original): 409 estável para consultar o evento existente.
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

    // Regra exclusiva de CRIAÇÃO (depende do relógio): a partir do primeiro dia do mês atual (UTC), não de "hoje".
    // Fica fora do validador para não impedir o replay de uma operação já registrada depois da virada do mês.
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

    // ---------------------------------------------------------------- PUT

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

                // (1) coordenador do passeio (serializa com POST de outro cadastro do grupo e com outros PUT/DELETE),
                // (2) valida a versão e trava o cadastro; a partir daqui pagamentos/versão não mudam até o commit.
                await TravarGrupoDoEventoAsync(id, ct);
                await TravarCadastroAsync(id, versao, request.UsuarioResponsavelId, agora, ct);
                var evento = await db.Eventos.Include(e => e.Membros.Where(m => m.Ativo)).SingleAsync(e => e.Id == id, ct);
                var participantes = evento.Membros.ToList(); // o Include já traz só os ativos
                var lancamentos = await db.Lancamentos.Where(l => l.EventoId == id && l.Ativo).ToListAsync(ct);

                var atuais = participantes.Select(p => p.MembroId).ToHashSet();
                var novos = conteudo.Membros.ToHashSet();
                var removidos = participantes.Where(p => !novos.Contains(p.MembroId)).ToList();
                var adicionados = conteudo.Membros.Where(m => !atuais.Contains(m)).ToList();

                var dataMudou = conteudo.DataEvento != evento.DataEvento;
                var localMudou = !string.Equals(conteudo.Local, evento.Local, StringComparison.Ordinal);
                // Compara a COMPOSIÇÃO (valores efetivos já normalizados e os booleanos), não só o total: redistribuir
                // 10+8+2 para 5+13+2 mantém 20, mas muda o que foi cobrado. Um número ignorado por um booleano
                // inalterado já chegou como zero, então não vira alteração financeira fictícia.
                var custoMudou = conteudo.TransporteEhGratis != evento.TransporteEhGratis
                    || conteudo.TransporteValor != evento.TransporteValor
                    || conteudo.AlimentacaoIndividual != evento.AlimentacaoIndividual
                    || conteudo.AlimentacaoValor != evento.AlimentacaoValor
                    || conteudo.SeguroObrigatorio != evento.SeguroObrigatorio
                    || conteudo.ValorPorMembro != evento.ValorPorMembro;
                var financeiroMudou = custoMudou || dataMudou || removidos.Count > 0 || adicionados.Count > 0;

                // Pago/Atrasado protege valores, participantes e vencimento: nunca reverte pagamento nem estorna.
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
                        lancamento.Ativo = false; // auditado pelo trigger de Lancamentos (contexto abaixo)
                        lancamento.DataAtualizacao = agora;
                        lancamento.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
                        continue;
                    }

                    // Pago/Atrasado: só chega aqui quando a alteração é apenas do local. Valor, vencimento e status
                    // não são tocados, mas a descrição acompanha o local corrigido, como nos lançamentos pendentes.
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

    // ---------------------------------------------------------------- DELETE

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

            // Ordem obrigatória: o trigger de eventos tira o snapshot dos participantes/lançamentos ATIVOS
            // (historico_eventos), então o evento é desativado primeiro; participantes e lançamentos depois.
            // Os triggers (eventos e Lancamentos) leem o mesmo SESSION_CONTEXT: responsável, IP e motivo.
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

    // ---------------------------------------------------------------- infraestrutura

    // Coordenador do passeio: lock de aplicação no SQL Server (vale entre instâncias da API), exclusivo, preso à transação
    // atual (liberado no commit/rollback), com espera limitada. Deve ser o PRIMEIRO lock do fluxo (ver ordem no topo).
    private async Task TravarGrupoAsync(Guid grupoId, CancellationToken ct)
    {
        var codigo = new SqlParameter("@codigo", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "EXEC @codigo = sys.sp_getapplock @Resource = @recurso, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @espera;",
            [codigo, new SqlParameter("@recurso", RecursoDoGrupo(grupoId)), new SqlParameter("@espera", lockOptions.TimeoutMs)], ct);

        var resultado = (int)codigo.Value;
        if (resultado >= 0)
        {
            return; // 0: concedido; 1: concedido depois de esperar
        }

        if (resultado == -999)
        {
            throw new InvalidOperationException("sp_getapplock recusou os parâmetros do lock do passeio.");
        }

        // -1 tempo esgotado, -2 cancelado, -3 vítima de deadlock: desiste com 409, sem gravar nada (a transação reverte).
        logger.LogWarning("Lock do passeio {EventoGrupoId} não adquirido (sp_getapplock {Resultado}).", grupoId, resultado);
        throw ApiProblemException.Conflict("Passeio em uso por outra operação.",
            "Outra operação está alterando este passeio agora. Nada foi alterado; tente novamente em instantes.");
    }

    // O EventoGrupoId é imutável: pode ser lido antes do lock. Inexistente: 404 (mesmo resultado de TravarCadastroAsync).
    private async Task TravarGrupoDoEventoAsync(Guid eventoId, CancellationToken ct)
    {
        var grupoId = await db.Eventos.AsNoTracking().Where(e => e.Id == eventoId).Select(e => (Guid?)e.EventoGrupoId).SingleOrDefaultAsync(ct)
            ?? throw ApiProblemException.NotFound("Evento não encontrado.");
        await TravarGrupoAsync(grupoId, ct);
    }

    // UPDATE que valida a rowversion e adquire o lock do cadastro. 0 linhas: inexistente/inativo (404) ou
    // versão desatualizada (409) — nunca sobrescreve silenciosamente a alteração de outra operação.
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
        var existentes = await db.Usuarios.AsNoTracking().Where(u => membros.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        var faltantes = membros.Except(existentes).OrderBy(m => m).ToList();
        if (faltantes.Count > 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "Dados inválidos.",
                errors: new Dictionary<string, string[]> { ["Membros"] = ["membros inexistentes: " + string.Join(", ", faltantes) + "."] },
                extensions: new Dictionary<string, object?> { ["membrosInexistentes"] = faltantes });
        }
    }

    // Um membro só pode estar em UM grupo ativo por eventoGrupoId. O índice único filtrado do banco é a
    // proteção definitiva contra corrida; esta consulta produz a mensagem com os IDs em conflito.
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

    // Tudo o que a cobrança tem de fixo vem do servidor; o cliente nunca escolhe status, categoria etc.
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

    // Executa o trabalho em uma transação, sobre uma conexão FIXADA (OpenConnection): o SESSION_CONTEXT
    // definido para os triggers e a limpeza no finally acontecem na MESMA conexão física. Se a limpeza falhar,
    // o pool da conexão é descartado para não reaproveitar identidade/IP de outra operação.
    // A execution strategy (retry do Aspire) exige que a unidade de trabalho inteira esteja no delegate.
    private async Task<T> ExecutarAsync<T>(Func<SessaoAuditoria, Task<T>> trabalho, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            throw new NotSupportedException("Eventos exigem SQL Server (transação, rowversion, trigger e SESSION_CONTEXT).");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var sessao = new SessaoAuditoria(db);
            await db.Database.OpenConnectionAsync(ct);
            var conexao = db.Database.GetDbConnection() as SqlConnection;
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var resultado = await trabalho(sessao);
                await tx.CommitAsync(ct);
                return resultado;
            }
            finally
            {
                try
                {
                    var limpo = await sessao.LimparAsync(logger);
                    if (!limpo && conexao is not null)
                    {
                        // Marcar a conexão ainda emprestada para descarte ANTES de devolvê-la ao pool.
                        SqlConnection.ClearPool(conexao);
                    }
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            }
        });
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
