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

        // Lançamento gerado por evento: valor, vencimento, finalidade, categoria e fluxo só mudam via /api/Eventos.
        // O status continua neste fluxo financeiro.
        if (entity.EventoId is not null)
        {
            ExigirSemAlteracaoDeEvento(entity, request);
        }

        var statusMudou = request.Status.HasValue && request.Status.Value != entity.Status;
        if (entity.EventoId is { } eventoId && statusMudou && db.Database.IsRelational())
        {
            return await AtualizarStatusDeEventoAsync(id, eventoId, request, ct);
        }

        AplicarAlteracoes(entity, request);
        entity.DataAtualizacao = clock.GetUtcNow().UtcDateTime;
        entity.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
        await db.SaveChangesAsync(ct);
        return LancamentoMapping.ToDto(entity);
    }

    // A mudança de status de um lançamento de evento altera a versão (rowversion) do cadastro: um PUT/DELETE do
    // evento feito com a versão anterior ao pagamento recebe 409 em vez de sobrescrever a situação financeira.
    // O evento é tocado ANTES do lançamento, a mesma ordem de locks de EventosService (evita deadlock; ver a ordem
    // completa no topo daquela classe — o pagamento usa apenas os níveis 2 e 3).
    //
    // Cada tentativa da execution strategy (retry do Aspire) parte de um ChangeTracker LIMPO e recarrega, sob o lock do
    // evento, tudo o que decide a gravação; nada lido antes do lock é reaproveitado. Assim, se a transação for revertida
    // e a operação repetida, o status volta a ser aplicado e gravado (o SaveChanges de uma tentativa revertida não deixa
    // a entidade "aceita" para a seguinte). Se o commit falhar de forma ambígua (a confirmação não chegou), a tentativa
    // seguinte primeiro verifica no banco se a operação foi efetivada e, só então, devolve sucesso com o estado real.
    private async Task<LancamentoDto> AtualizarStatusDeEventoAsync(Guid lancamentoId, Guid eventoId, UpdateLancamentoRequest request, CancellationToken ct)
    {
        var agora = clock.GetUtcNow().UtcDateTime;
        var commitIncerto = false;
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            if (commitIncerto)
            {
                var efetivado = await LerSeEfetivadoAsync(lancamentoId, eventoId, request, agora, ct);
                if (efetivado is not null)
                {
                    return efetivado;
                }

                commitIncerto = false; // o commit não chegou a valer: refaz a operação inteira
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var tocados = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.eventos SET AtualizadoEmUtc = {agora} WHERE Id = {eventoId} AND Ativo = 1", ct);
            if (tocados == 0)
            {
                // O evento foi excluído (ou desativado) entre a leitura do lançamento e o lock: nada de gravar status
                // numa cobrança que já foi desativada junto com o cadastro. O rollback desfaz qualquer coisa pendente.
                throw ApiProblemException.NotFound("Lançamento não encontrado.");
            }

            // Com o evento travado, tudo é relido: um PUT concorrente que removeu este participante ou mudou custos já
            // foi confirmado, e a entidade lida antes do lock pode estar obsoleta.
            var entity = await db.Lancamentos.Include(l => l.Membro)
                .SingleOrDefaultAsync(l => l.Id == lancamentoId && l.Ativo && l.EventoId == eventoId, ct);
            if (entity is null ||
                !await db.EventosMembros.AsNoTracking().AnyAsync(m => m.EventoId == eventoId && m.LancamentoId == lancamentoId && m.Ativo, ct))
            {
                throw ApiProblemException.NotFound("Lançamento não encontrado.");
            }

            ExigirSemAlteracaoDeEvento(entity, request);
            AplicarAlteracoes(entity, request);
            entity.DataAtualizacao = agora;
            entity.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
            await db.SaveChangesAsync(ct);

            var dto = LancamentoMapping.ToDto(entity);
            commitIncerto = true;   // a partir daqui, uma falha no commit pode ter ocorrido antes OU depois de efetivar
            await tx.CommitAsync(ct);
            commitIncerto = false;
            return dto;
        });
    }

    // Marca da operação efetivada: status pedido, instante desta operação e o responsável. Nada disso existe se a
    // transação foi revertida. A resposta é montada do que está no banco, nunca de dados presumidos.
    private async Task<LancamentoDto?> LerSeEfetivadoAsync(Guid lancamentoId, Guid eventoId, UpdateLancamentoRequest request, DateTime agora, CancellationToken ct)
    {
        var status = request.Status!.Value;
        var entity = await db.Lancamentos.AsNoTracking().Include(l => l.Membro)
            .SingleOrDefaultAsync(l => l.Id == lancamentoId && l.EventoId == eventoId && l.Ativo && l.Status == status
                && l.DataAtualizacao == agora && l.AtualizadoPorUsuarioId == request.UsuarioResponsavelId, ct);
        return entity is null ? null : LancamentoMapping.ToDto(entity);
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
