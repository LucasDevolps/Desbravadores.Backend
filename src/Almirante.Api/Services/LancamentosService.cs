using System.Globalization;
using System.Linq.Expressions;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentosService(
    AlmiranteDbContext db,
    TimeProvider clock,
    LancamentoGeralService lancamentoGeral,
    LancamentoExclusaoService exclusao,
    SinalAvisosNotificacoes sinal,
    DataCivil dataCivil)
{
    public async Task<LancamentosResponse> ListAsync(int page, int pageSize, string? search, string? status,
        string? finalidade, DateOnly? vencimento, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = Filtrar(db.Lancamentos.AsNoTracking().Where(l => l.Ativo), search, status, finalidade, vencimento);
        var total = await query.CountAsync(ct);
        // Sem filtros (issue #99): Atrasado, depois Pendente, depois Pago; o vencimento mais recente segue como critério secundário.
        var ordenada = SemFiltros(search, status, finalidade, vencimento)
            ? query.OrderBy(l => l.Status == StatusLancamento.Atrasado ? 0 : l.Status == StatusLancamento.Pendente ? 1 : 2)
                .ThenByDescending(l => l.Vencimento)
            : query.OrderByDescending(l => l.Vencimento);
        var items = await ordenada
            .ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(LancamentoMapping.Projection)
            .ToListAsync(ct);

        return new LancamentosResponse
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
        };
    }

    public Task<LancamentoDto?> GetByIdAsync(Guid id, CancellationToken ct) => db.Lancamentos
        .AsNoTracking()
        .Where(l => l.Id == id && l.Ativo)
        .Select(LancamentoMapping.Projection)
        .SingleOrDefaultAsync(ct);

    public async Task<RegistrarLancamentoResult> RegistrarAsync(RegistrarLancamentoRequest request, CancellationToken ct)
    {
        if (request.AplicarATodosOsMembros)
        {
            return await lancamentoGeral.RegistrarAsync(request, ct);
        }

        // Idempotency-Key é opcional aqui: sem ela, cada POST é uma nova cobrança legítima (nada é deduplicado por conteúdo);
        // com ela, o reenvio da mesma solicitação devolve o lançamento já criado.
        var chave = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        var hash = chave is null ? null : LancamentoGeralService.CalcularHash(request);
        if (chave is not null && await LerOperacaoAsync(chave, ct) is { } registrada)
        {
            return await ReutilizarAsync(registrada, hash!, null, ct);
        }

        var membro = await db.Usuarios.AsNoTracking()
            .Where(u => u.Id == request.MembroId && u.Ativo)
            .Select(u => new { u.Id, u.Nome })
            .SingleOrDefaultAsync(ct);
        if (membro is null)
        {
            return new(RegistrarLancamentoOutcome.MembroNaoEncontrado, null, null);
        }

        var agora = clock.GetUtcNow().UtcDateTime;
        var operacaoId = chave is null ? (Guid?)null : Guid.NewGuid();
        var entity = LancamentoMapping.Criar(request, membro.Id, operacaoId, agora);
        db.Lancamentos.Add(entity);
        if (operacaoId is { } id)
        {
            db.LancamentosOperacoes.Add(LancamentoGeralService.NovaOperacao(request, id, chave!, hash!, 1, 1, agora));
        }

        var notificacoes = NotificacoesFabrica.ParaDebitos([entity], agora);
        db.Notificacoes.AddRange(notificacoes);
        var notificar = notificacoes.Count > 0;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (chave is not null && await LerOperacaoAsync(chave, ct) is { } concorrente)
            {
                return await ReutilizarAsync(concorrente, hash!, operacaoId, ct);
            }

            // Commit efetivado com a confirmação perdida: a retentativa da execution strategy esbarra no próprio lançamento.
            if (!await db.Lancamentos.AsNoTracking().AnyAsync(l => l.Id == entity.Id, ct))
            {
                throw;
            }
        }

        if (notificar)
        {
            sinal.Sinalizar();
        }

        var response = LancamentoMapping.ToDto(entity) with { MembroNome = membro.Nome };
        return new(RegistrarLancamentoOutcome.LancamentoUnicoCriado, response, null);
    }

    private Task<LancamentoOperacao?> LerOperacaoAsync(string chave, CancellationToken ct) =>
        db.LancamentosOperacoes.AsNoTracking().SingleOrDefaultAsync(o => o.IdempotencyKey == chave, ct);

    /// <summary>Reenvio com a mesma chave: mesmos dados devolvem o lançamento da operação (201 se for a própria tentativa).</summary>
    private async Task<RegistrarLancamentoResult> ReutilizarAsync(LancamentoOperacao operacao, string hash, Guid? propriaOperacaoId, CancellationToken ct)
    {
        if (!string.Equals(operacao.RequestHash, hash, StringComparison.Ordinal))
        {
            return new(RegistrarLancamentoOutcome.GeralConflitoIdempotencia, null, null);
        }

        var lancamento = await db.Lancamentos.AsNoTracking().Where(l => l.OperacaoId == operacao.Id)
            .Select(LancamentoMapping.Projection).SingleAsync(ct);
        if (!await db.Lancamentos.AsNoTracking().AnyAsync(l => l.Id == lancamento.Id && l.Ativo, ct))
        {
            throw ApiProblemException.Conflict("Lançamento da operação já foi excluído.",
                "Esta operação idempotente já criou um lançamento que depois foi excluído; use uma nova Idempotency-Key para cadastrar novamente.");
        }

        return operacao.Id == propriaOperacaoId
            ? new(RegistrarLancamentoOutcome.LancamentoUnicoCriado, lancamento, null)
            : new(RegistrarLancamentoOutcome.LancamentoUnicoReutilizado, lancamento, null);
    }

    public async Task<LancamentoDto?> UpdateAsync(Guid id, UpdateLancamentoRequest request, CancellationToken ct)
    {
        var entity = await db.Lancamentos.Include(l => l.Membro).SingleOrDefaultAsync(l => l.Id == id && l.Ativo, ct);
        if (entity is null)
        {
            return null;
        }

        if (entity.LancamentoOrigemId is not null)
        {
            throw DevolucaoConflito();
        }

        var statusMudou = request.Status.HasValue && request.Status.Value != entity.Status;
        var valorMudou = request.Valor.HasValue && request.Valor.Value != entity.Valor;
        if (entity.EventoId is not null)
        {
            ExigirSemAlteracaoDeEvento(entity, request);
            if (valorMudou)
            {
                if (!EventoRegras.ValorValido(request.Valor!.Value))
                {
                    throw ApiProblemException.Invalid("valor",
                        "valor da cobrança de evento deve ter no máximo 2 casas decimais e caber em decimal(18,2).");
                }

                ExigirCobrancaAjustavel(entity);
            }
        }

        ExigirSemAlteracaoDePagamento(entity, request);
        var hoje = dataCivil.Hoje();
        ExigirPendenteNaoVencido(entity, request, hoje);

        if (entity.EventoId is { } eventoId && (statusMudou || valorMudou) && db.Database.IsRelational())
        {
            var status = request.Status ?? entity.Status;
            var valor = request.Valor ?? entity.Valor;
            return await AtualizarComTravaDoEventoAsync(id, eventoId, request.UsuarioResponsavelId, (lancamento, _, valorPorMembro) =>
            {
                ExigirSemAlteracaoDeEvento(lancamento, request);
                var ajustaValor = request.Valor.HasValue && request.Valor.Value != lancamento.Valor;
                if (ajustaValor)
                {
                    ExigirCobrancaAjustavel(lancamento);
                }

                ExigirSemAlteracaoDePagamento(lancamento, request);
                ExigirPendenteNaoVencido(lancamento, request, hoje);
                if (ajustaValor)
                {
                    lancamento.ValorAjustado = request.Valor!.Value != valorPorMembro;
                }

                AplicarAlteracoes(lancamento, request);
            }, l => l.Status == status && l.Valor == valor, ct);
        }

        AplicarAlteracoes(entity, request);
        entity.DataAtualizacao = clock.GetUtcNow().UtcDateTime;
        entity.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
        await SalvarAsync(ct);
        return LancamentoMapping.ToDto(entity);
    }

    public Task<LancamentoDto?> RegistrarPagamentoAsync(RegistrarPagamentoRequest request, CancellationToken ct) =>
        AplicarPagamentoAsync(request, registro: true, ct);

    public Task<LancamentoDto?> AtualizarPagamentoAsync(AtualizarPagamentoRequest request, CancellationToken ct) =>
        AplicarPagamentoAsync(request, registro: false, ct);

    private async Task<LancamentoDto?> AplicarPagamentoAsync(PagamentoLancamentoRequest request, bool registro, CancellationToken ct)
    {
        var entity = await db.Lancamentos.Include(l => l.Membro).SingleOrDefaultAsync(l => l.Id == request.Id && l.Ativo, ct);
        if (entity is null)
        {
            return null;
        }

        if (entity.LancamentoOrigemId is not null)
        {
            throw DevolucaoConflito();
        }

        var dataPagamento = request.DataPagamento!.Value;
        var valorPago = request.ValorPago!.Value;
        var usuarioId = request.UsuarioResponsavelId;
        ValidarPagamento(entity, valorPago, registro);

        // Issue #209: as devoluções foram calculadas sobre o valor pago; mudá-lo depois deixaria a devolução maior que o que entrou.
        if (!registro && valorPago != entity.ValorPago &&
            await db.Lancamentos.AnyAsync(l => l.LancamentoOrigemId == entity.Id && l.Ativo, ct))
        {
            throw ApiProblemException.Conflict("Pagamento com devolução.",
                "O valor pago não pode ser corrigido depois que uma devolução foi gerada sobre ele. A data do pagamento ainda pode ser corrigida.");
        }

        void Aplicar(Lancamento lancamento, DateTime agora)
        {
            ValidarPagamento(lancamento, valorPago, registro);
            lancamento.Status = StatusLancamento.Pago;
            lancamento.DataPagamento = dataPagamento;
            lancamento.ValorPago = valorPago;
            lancamento.PagamentoRegistradoPorUsuarioId = usuarioId;
            lancamento.PagamentoRegistradoEmUtc = agora;
        }

        if (entity.EventoId is { } eventoId && db.Database.IsRelational())
        {
            return await AtualizarComTravaDoEventoAsync(entity.Id, eventoId, usuarioId, (l, a, _) => Aplicar(l, a),
                l => l.Status == StatusLancamento.Pago && l.DataPagamento == dataPagamento && l.ValorPago == valorPago
                    && l.PagamentoRegistradoPorUsuarioId == usuarioId, ct);
        }

        var agora = clock.GetUtcNow().UtcDateTime;
        Aplicar(entity, agora);
        entity.DataAtualizacao = agora;
        entity.AtualizadoPorUsuarioId = usuarioId;
        await SalvarAsync(ct);
        return LancamentoMapping.ToDto(entity);
    }

    public Task<LancamentoDto?> RegistrarDevolucaoAsync(RegistrarDevolucaoRequest request, CancellationToken ct) =>
        AplicarDevolucaoAsync(request, registro: true, ct);

    public Task<LancamentoDto?> AtualizarDevolucaoAsync(AtualizarDevolucaoRequest request, CancellationToken ct) =>
        AplicarDevolucaoAsync(request, registro: false, ct);

    /// <summary>
    /// Issue #209: baixa (ou correção da baixa) de uma devolução de evento. O tesoureiro digita o valor devolvido, até o esperado; quando
    /// ele difere do esperado, a justificativa é obrigatória. Formato da justificativa e data são conferidos no validador.
    /// </summary>
    private async Task<LancamentoDto?> AplicarDevolucaoAsync(DevolucaoLancamentoRequest request, bool registro, CancellationToken ct)
    {
        var entity = await db.Lancamentos.Include(l => l.Membro).SingleOrDefaultAsync(l => l.Id == request.Id && l.Ativo, ct);
        if (entity is null)
        {
            return null;
        }

        var dataDevolucao = request.DataPagamento!.Value;
        var valorDevolvido = request.ValorPago!.Value;
        var justificativa = DevolucaoRegras.NormalizarJustificativa(request.Justificativa);
        var usuarioId = request.UsuarioResponsavelId;
        ValidarDevolucao(entity, valorDevolvido, justificativa, registro);

        void Aplicar(Lancamento lancamento, DateTime agora)
        {
            ValidarDevolucao(lancamento, valorDevolvido, justificativa, registro);
            lancamento.Status = StatusLancamento.Pago;
            lancamento.DataPagamento = dataDevolucao;
            lancamento.ValorPago = valorDevolvido;
            lancamento.DevolucaoJustificativa = justificativa;
            lancamento.PagamentoRegistradoPorUsuarioId = usuarioId;
            lancamento.PagamentoRegistradoEmUtc = agora;
        }

        if (db.Database.IsRelational())
        {
            return await AtualizarComTravaDoEventoAsync(entity.Id, entity.EventoId!.Value, usuarioId, (l, a, _) => Aplicar(l, a),
                l => l.Status == StatusLancamento.Pago && l.DataPagamento == dataDevolucao && l.ValorPago == valorDevolvido
                    && l.DevolucaoJustificativa == justificativa && l.PagamentoRegistradoPorUsuarioId == usuarioId, ct, exigirParticipante: false);
        }

        var agora = clock.GetUtcNow().UtcDateTime;
        Aplicar(entity, agora);
        entity.DataAtualizacao = agora;
        entity.AtualizadoPorUsuarioId = usuarioId;
        await SalvarAsync(ct);
        return LancamentoMapping.ToDto(entity);
    }

    private static void ValidarDevolucao(Lancamento lancamento, decimal valorDevolvido, string? justificativa, bool registro)
    {
        if (lancamento.LancamentoOrigemId is null)
        {
            throw ApiProblemException.Conflict("Lançamento não é uma devolução.",
                "Só devoluções de evento recebem baixa por este endpoint. Para registrar um pagamento, use POST /api/Lancamentos/{id}/Pagamento.");
        }

        if (registro && lancamento.DataPagamento is not null)
        {
            throw ApiProblemException.Conflict("Devolução já registrada.",
                "Esta devolução já tem baixa registrada. Para corrigir data, valor ou justificativa, use PUT /api/Lancamentos/{id}/Devolucao.");
        }

        if (!registro && lancamento.DataPagamento is null)
        {
            throw ApiProblemException.Conflict("Devolução não registrada.",
                "Esta devolução ainda não tem baixa registrada. Use POST /api/Lancamentos/{id}/Devolucao.");
        }

        if (valorDevolvido > lancamento.Valor)
        {
            throw ApiProblemException.Invalid("valorPago",
                $"valorPago não pode ser maior que o valor esperado da devolução ({lancamento.Valor.ToString("F2", CultureInfo.InvariantCulture)}).");
        }

        if (justificativa is null && DevolucaoRegras.ExigeJustificativa(lancamento.Valor, valorDevolvido))
        {
            throw ApiProblemException.Invalid("justificativa",
                $"justificativa é obrigatória quando o valor devolvido é diferente do esperado ({lancamento.Valor.ToString("F2", CultureInfo.InvariantCulture)}).");
        }
    }

    internal static ApiProblemException DevolucaoConflito() => ApiProblemException.Conflict("Lançamento de devolução.",
        "Devolução de evento não muda por este endpoint: a baixa e a correção são feitas em /api/Lancamentos/{id}/Devolucao, e valor, " +
        "membro e vencimento vêm do evento.");

    /// <summary>
    /// Altera um lançamento de evento depois de travar a linha do evento (mesma ordem de locks de /api/Eventos),
    /// com retentativa segura: se o commit ficou incerto, devolve o estado gravado em vez de repetir. Cobrança precisa estar
    /// ligada a um participante ativo; devolução (<paramref name="exigirParticipante"/> false) só precisa estar ativa no evento.
    /// </summary>
    private async Task<LancamentoDto> AtualizarComTravaDoEventoAsync(Guid lancamentoId, Guid eventoId, Guid usuarioId,
        Action<Lancamento, DateTime, decimal> alterar, Expression<Func<Lancamento, bool>> jaEfetivado, CancellationToken ct,
        bool exigirParticipante = true)
    {
        var agora = clock.GetUtcNow().UtcDateTime;
        var commitIncerto = false;
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            if (commitIncerto)
            {
                var efetivado = await db.Lancamentos.AsNoTracking().Include(l => l.Membro)
                    .Where(l => l.Id == lancamentoId && l.EventoId == eventoId && l.Ativo
                        && l.DataAtualizacao == agora && l.AtualizadoPorUsuarioId == usuarioId)
                    .Where(jaEfetivado)
                    .SingleOrDefaultAsync(ct);
                if (efetivado is not null)
                {
                    return LancamentoMapping.ToDto(efetivado);
                }

                commitIncerto = false;
            }

            LancamentoDto dto;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var tocados = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE dbo.eventos SET AtualizadoEmUtc = {agora} WHERE Id = {eventoId} AND Ativo = 1", ct);
                if (tocados == 0)
                {
                    throw ApiProblemException.NotFound("Lançamento não encontrado.");
                }

                var entity = await db.Lancamentos.Include(l => l.Membro)
                    .SingleOrDefaultAsync(l => l.Id == lancamentoId && l.Ativo && l.EventoId == eventoId, ct);
                if (entity is null || (exigirParticipante &&
                    !await db.EventosMembros.AsNoTracking().AnyAsync(m => m.EventoId == eventoId && m.LancamentoId == lancamentoId && m.Ativo, ct)))
                {
                    throw ApiProblemException.NotFound("Lançamento não encontrado.");
                }

                var valorPorMembro = await db.Eventos.AsNoTracking().Where(e => e.Id == eventoId)
                    .Select(e => e.ValorPorMembro).SingleAsync(ct);
                alterar(entity, agora, valorPorMembro);
                entity.DataAtualizacao = agora;
                entity.AtualizadoPorUsuarioId = usuarioId;
                await SalvarAsync(ct);

                dto = LancamentoMapping.ToDto(entity);
                commitIncerto = true;
                await tx.CommitAsync(ct);
                commitIncerto = false;
            }

            return dto;
        });
    }

    private async Task SalvarAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiProblemException.Conflict("Lançamento alterado por outra operação.",
                "Status, valor ou pagamento deste lançamento mudaram durante a requisição. Consulte o lançamento e tente novamente.");
        }
    }

    private static void ValidarPagamento(Lancamento lancamento, decimal valorPago, bool registro)
    {
        if (registro && lancamento.DataPagamento is not null)
        {
            throw ApiProblemException.Conflict("Pagamento já registrado.",
                "Este lançamento já possui pagamento registrado. Para corrigir data ou valor, use PUT /api/Lancamentos/{id}/Pagamento.");
        }

        if (!registro && lancamento.DataPagamento is null)
        {
            throw ApiProblemException.Conflict("Pagamento não registrado.",
                "Este lançamento ainda não possui pagamento registrado. Use POST /api/Lancamentos/{id}/Pagamento.");
        }

        if (valorPago > lancamento.Valor)
        {
            throw ApiProblemException.Invalid("valorPago",
                $"valorPago não pode ser maior que o valor do lançamento ({lancamento.Valor.ToString("F2", CultureInfo.InvariantCulture)}).");
        }
    }

    private static void ExigirSemAlteracaoDePagamento(Lancamento entity, UpdateLancamentoRequest request)
    {
        var statusMudou = request.Status.HasValue && request.Status.Value != entity.Status;
        if (statusMudou && request.Status!.Value == StatusLancamento.Pago)
        {
            throw ApiProblemException.Conflict("Pagamento deve ser registrado pelo endpoint dedicado.",
                "Para marcar como Pago, use POST /api/Lancamentos/{id}/Pagamento informando dataPagamento e valorPago.");
        }

        var alteraPagamento = statusMudou
            || (request.Valor.HasValue && request.Valor.Value != entity.Valor)
            || (request.TipoFluxo.HasValue && request.TipoFluxo.Value != entity.TipoFluxo);
        if (entity.DataPagamento is not null && alteraPagamento)
        {
            throw ApiProblemException.Conflict("Lançamento com pagamento registrado.",
                "Status, valor e tipo de fluxo de um lançamento com pagamento registrado não mudam por este endpoint. Para corrigir o pagamento, use PUT /api/Lancamentos/{id}/Pagamento.");
        }
    }

    /// <summary>
    /// Issue #184: a rotina de vencimento marcaria de novo como Atrasado um lançamento posto em Pendente com vencimento passado. Só a
    /// troca para Pendente é recusada; reenviar o status atual, ou mudar só o vencimento, segue aceito.
    /// </summary>
    private static void ExigirPendenteNaoVencido(Lancamento entity, UpdateLancamentoRequest request, DateOnly hoje)
    {
        var vencimento = request.Vencimento ?? entity.Vencimento;
        if (request.Status == StatusLancamento.Pendente && entity.Status != StatusLancamento.Pendente && vencimento < hoje)
        {
            throw ApiProblemException.Invalid("status",
                $"status Pendente exige vencimento a partir de {hoje.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (hoje no fuso do clube); " +
                "um lançamento vencido e não pago fica Atrasado.");
        }
    }

    private static void ExigirCobrancaAjustavel(Lancamento cobranca)
    {
        if (cobranca.Status == StatusLancamento.Pago || cobranca.DataPagamento is not null)
        {
            throw ApiProblemException.Conflict("Cobrança com pagamento registrado.",
                "O valor de uma cobrança de evento só pode ser ajustado antes de qualquer pagamento, inclusive parcial. Para devolver valores " +
                "já pagos, use o estorno do participante (POST /api/Eventos/{id}/Participantes/{membroId}/Estorno) ou a redução do evento.",
                new Dictionary<string, object?> { ["codigo"] = CodigoCobrancaComPagamento, ["lancamentoId"] = cobranca.Id });
        }
    }

    public const string CodigoCobrancaComPagamento = "COBRANCA_EVENTO_COM_PAGAMENTO";

    private static void ExigirSemAlteracaoDeEvento(Lancamento entity, UpdateLancamentoRequest request)
    {
        var altera = (request.Finalidade is not null && request.Finalidade != entity.Finalidade)
            || (request.Categoria.HasValue && request.Categoria.Value != entity.Categoria)
            || (request.TipoFluxo.HasValue && request.TipoFluxo.Value != entity.TipoFluxo)
            || (request.Vencimento.HasValue && request.Vencimento.Value != entity.Vencimento);
        if (altera)
        {
            throw LancamentoDeEventoConflito();
        }
    }

    internal static ApiProblemException LancamentoDeEventoConflito() => ApiProblemException.Conflict(
        "Lançamento vinculado a um evento.",
        "Membro, vencimento, finalidade, categoria e tipo de fluxo de um lançamento gerado por evento vêm de /api/Eventos. Aqui só mudam o status, " +
        "o valor da cobrança antes de qualquer pagamento (ajuste individual) e a exclusão da cobrança sem pagamento (isenção individual).");

    public Task<bool> DeleteAsync(DeleteLancamentoRequest request, CancellationToken ct) => exclusao.DeleteAsync(request, ct);

    // Mesmos critérios de Filtrar: texto em branco e "Todos" não contam como filtro ativo.
    private static bool SemFiltros(string? search, string? status, string? finalidade, DateOnly? vencimento) =>
        string.IsNullOrWhiteSpace(search) &&
        (string.IsNullOrWhiteSpace(status) || status == "Todos") &&
        (string.IsNullOrWhiteSpace(finalidade) || finalidade == "Todos") &&
        !vencimento.HasValue;

    private static IQueryable<Lancamento> Filtrar(IQueryable<Lancamento> query, string? search, string? status,
        string? finalidade, DateOnly? vencimento)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            var categorias = Enum.GetValues<CategoriaLancamento>()
                .Where(c => c.ToString().ToLowerInvariant().Contains(term)).ToArray();
            query = query.Where(l => (l.Membro != null && l.Membro.Nome.ToLower().Contains(term)) ||
                (l.Descricao != null && l.Descricao.ToLower().Contains(term)) ||
                (l.Finalidade != null && l.Finalidade.ToLower().Contains(term)) || categorias.Contains(l.Categoria));
        }

        if (!string.IsNullOrWhiteSpace(status) && status != "Todos")
        {
            query = Enum.TryParse<StatusLancamento>(status, true, out var parsed) && Enum.IsDefined(parsed)
                ? query.Where(l => l.Status == parsed)
                : query.Where(l => false);
        }

        if (!string.IsNullOrWhiteSpace(finalidade) && finalidade != "Todos")
        {
            query = query.Where(l => l.Finalidade == finalidade);
        }

        if (vencimento.HasValue)
        {
            query = query.Where(l => l.Vencimento == vencimento.Value);
        }

        return query;
    }

    private static void AplicarAlteracoes(Lancamento entity, UpdateLancamentoRequest request)
    {
        if (request.Finalidade is not null) entity.Finalidade = request.Finalidade;
        if (request.Descricao is not null) entity.Descricao = request.Descricao;
        if (request.Categoria.HasValue) entity.Categoria = request.Categoria.Value;
        if (request.TipoFluxo.HasValue) entity.TipoFluxo = request.TipoFluxo.Value;
        if (request.Valor.HasValue) entity.Valor = request.Valor.Value;
        if (request.Vencimento.HasValue) entity.Vencimento = request.Vencimento.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
    }
}
