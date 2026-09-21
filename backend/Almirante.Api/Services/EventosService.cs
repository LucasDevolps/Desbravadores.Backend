using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

// Regras de negócio de /api/Eventos. Toda escrita acontece em UMA transação SQL (evento, participantes,
// lançamentos, idempotência), com um único SaveChanges em lote (sem N+1) e sem chamar /Lancamentos por HTTP.
//
// Ordem de locks (evita deadlock com a mudança de status de lançamentos, ver LancamentosService.UpdateAsync):
//   1) UPDATE em eventos filtrado por Id/Ativo/Versao (valida a rowversion e trava a linha do evento);
//   2) leitura/alteração de evento_membros e Lancamentos.
// Quem altera o status de um lançamento de evento também toca a linha do evento ANTES, então a verificação
// "há lançamento Pago/Atrasado?" feita aqui, com o evento travado, não pode ser invalidada até o commit.
public sealed class EventosService(AlmiranteDbContext db, TimeProvider clock, ILogger<EventosService> logger)
{
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

        try
        {
            return await ExecutarAsync(async _ =>
            {
                var existente = await db.EventosOperacoes.AsNoTracking()
                    .SingleOrDefaultAsync(o => o.UsuarioId == usuarioId && o.IdempotencyKey == chave, ct);
                if (existente is not null)
                {
                    return await ReplayAsync(existente, hash, ct);
                }

                var grupoId = Guid.NewGuid();
                var eventoId = grupoId;
                if (request.EventoReferenciaId is { } referenciaId)
                {
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
                db.EventosOperacoes.Add(new EventoOperacao
                {
                    Id = Guid.NewGuid(),
                    UsuarioId = usuarioId,
                    IdempotencyKey = chave,
                    RequestHash = hash,
                    EventoId = eventoId,
                    CriadoEmUtc = agora,
                });

                await SalvarAsync(ct);

                logger.LogInformation("Evento {EventoId} (grupo {EventoGrupoId}) criado por {UsuarioId} com {Quantidade} lançamentos de {ValorPorMembro}.",
                    eventoId, grupoId, usuarioId, lancamentos.Count, evento.ValorPorMembro);
                return new RegistrarEventoResult(EventoMapping.ToDto(evento,
                    participantes.Select(p => p.MembroId),
                    lancamentos.Select(l => new EventoLancamentoDto(l.Id, l.MembroId!.Value, l.Valor, l.Status))), true);
            }, ct);
        }
        catch (ViolacaoUnicidadeException)
        {
            // Concorrência: outra requisição gravou primeiro a mesma chave (replay) ou o mesmo participante
            // no mesmo grupo (409). O estado já foi revertido; resolve lendo o que venceu.
            db.ChangeTracker.Clear();
            var existente = await db.EventosOperacoes.AsNoTracking()
                .SingleOrDefaultAsync(o => o.UsuarioId == usuarioId && o.IdempotencyKey == chave, ct);
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

    // Mesma chave + mesma operação lógica: devolve o cadastro já criado (200). Hash diferente: 409.
    // Cadastro excluído depois: nunca reativa nem recria — 409 sem efeitos.
    private async Task<RegistrarEventoResult> ReplayAsync(EventoOperacao operacao, string hash, CancellationToken ct)
    {
        if (!string.Equals(operacao.RequestHash, hash, StringComparison.Ordinal))
        {
            throw ApiProblemException.Conflict("Chave de idempotência já usada com dados diferentes.",
                "O header Idempotency-Key informado já foi usado em uma operação de evento com dados diferentes.");
        }

        var dto = await GetByIdAsync(operacao.EventoId, ct)
            ?? throw ApiProblemException.Conflict("Evento da operação já foi excluído.",
                "Esta operação idempotente já criou um evento que depois foi excluído; use uma nova Idempotency-Key para cadastrar novamente.");
        return new RegistrarEventoResult(dto, false);
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

                // (1) valida a versão e trava o cadastro; a partir daqui pagamentos/versão não mudam até o commit.
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
                var custoMudou = conteudo.ValorPorMembro != evento.ValorPorMembro;
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
                var limpo = await sessao.LimparAsync(logger);
                await db.Database.CloseConnectionAsync();
                if (!limpo && conexao is not null)
                {
                    SqlConnection.ClearPool(conexao);
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
