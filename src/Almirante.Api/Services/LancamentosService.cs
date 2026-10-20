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
    LancamentoExclusaoService exclusao)
{
    public async Task<LancamentosResponse> ListAsync(int page, int pageSize, string? search, string? status,
        string? finalidade, DateOnly? vencimento, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = Filtrar(db.Lancamentos.AsNoTracking().Where(l => l.Ativo), search, status, finalidade, vencimento);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.Vencimento)
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

        var membro = await db.Usuarios.AsNoTracking()
            .Where(u => u.Id == request.MembroId)
            .Select(u => new { u.Id, u.Nome })
            .SingleOrDefaultAsync(ct);
        if (membro is null)
        {
            return new(RegistrarLancamentoOutcome.MembroNaoEncontrado, null, null);
        }

        var entity = LancamentoMapping.Criar(request, membro.Id, null, clock.GetUtcNow().UtcDateTime);
        db.Lancamentos.Add(entity);
        await db.SaveChangesAsync(ct);
        var response = LancamentoMapping.ToDto(entity) with { MembroNome = membro.Nome };
        return new(RegistrarLancamentoOutcome.LancamentoUnicoCriado, response, null);
    }

    public async Task<LancamentoDto?> UpdateAsync(Guid id, UpdateLancamentoRequest request, CancellationToken ct)
    {
        var entity = await db.Lancamentos.Include(l => l.Membro).SingleOrDefaultAsync(l => l.Id == id && l.Ativo, ct);
        if (entity is null)
        {
            return null;
        }

        if (entity.EventoId is not null)
        {
            ExigirSemAlteracaoDeEvento(entity, request);
        }

        ExigirSemAlteracaoDePagamento(entity, request);

        var statusMudou = request.Status.HasValue && request.Status.Value != entity.Status;
        if (entity.EventoId is { } eventoId && statusMudou && db.Database.IsRelational())
        {
            var status = request.Status!.Value;
            return await AtualizarComTravaDoEventoAsync(id, eventoId, request.UsuarioResponsavelId, (lancamento, _) =>
            {
                ExigirSemAlteracaoDeEvento(lancamento, request);
                ExigirSemAlteracaoDePagamento(lancamento, request);
                AplicarAlteracoes(lancamento, request);
            }, l => l.Status == status, ct);
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

        var dataPagamento = request.DataPagamento!.Value;
        var valorPago = request.ValorPago!.Value;
        var usuarioId = request.UsuarioResponsavelId;
        ValidarPagamento(entity, valorPago, registro);

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
            return await AtualizarComTravaDoEventoAsync(entity.Id, eventoId, usuarioId, Aplicar,
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

    /// <summary>
    /// Altera um lançamento de evento depois de travar a linha do evento (mesma ordem de locks de /api/Eventos),
    /// com retentativa segura: se o commit ficou incerto, devolve o estado gravado em vez de repetir.
    /// </summary>
    private async Task<LancamentoDto> AtualizarComTravaDoEventoAsync(Guid lancamentoId, Guid eventoId, Guid usuarioId,
        Action<Lancamento, DateTime> alterar, Expression<Func<Lancamento, bool>> jaEfetivado, CancellationToken ct)
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

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var tocados = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.eventos SET AtualizadoEmUtc = {agora} WHERE Id = {eventoId} AND Ativo = 1", ct);
            if (tocados == 0)
            {
                throw ApiProblemException.NotFound("Lançamento não encontrado.");
            }

            var entity = await db.Lancamentos.Include(l => l.Membro)
                .SingleOrDefaultAsync(l => l.Id == lancamentoId && l.Ativo && l.EventoId == eventoId, ct);
            if (entity is null ||
                !await db.EventosMembros.AsNoTracking().AnyAsync(m => m.EventoId == eventoId && m.LancamentoId == lancamentoId && m.Ativo, ct))
            {
                throw ApiProblemException.NotFound("Lançamento não encontrado.");
            }

            alterar(entity, agora);
            entity.DataAtualizacao = agora;
            entity.AtualizadoPorUsuarioId = usuarioId;
            await SalvarAsync(ct);

            var dto = LancamentoMapping.ToDto(entity);
            commitIncerto = true;
            await tx.CommitAsync(ct);
            commitIncerto = false;
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


    private static void ExigirSemAlteracaoDeEvento(Lancamento entity, UpdateLancamentoRequest request)
    {
        var altera = (request.Finalidade is not null && request.Finalidade != entity.Finalidade)
            || (request.Categoria.HasValue && request.Categoria.Value != entity.Categoria)
            || (request.TipoFluxo.HasValue && request.TipoFluxo.Value != entity.TipoFluxo)
            || (request.Valor.HasValue && request.Valor.Value != entity.Valor)
            || (request.Vencimento.HasValue && request.Vencimento.Value != entity.Vencimento);
        if (altera)
        {
            throw LancamentoDeEventoConflito();
        }
    }

    internal static ApiProblemException LancamentoDeEventoConflito() => ApiProblemException.Conflict(
        "Lançamento vinculado a um evento.",
        "Valor, membro, vencimento, finalidade, categoria e exclusão de um lançamento gerado por evento devem ser feitos por meio de /api/Eventos. Apenas o status pode ser alterado aqui.");

    public Task<bool> DeleteAsync(DeleteLancamentoRequest request, CancellationToken ct) => exclusao.DeleteAsync(request, ct);

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
