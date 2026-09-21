using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
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

        AplicarAlteracoes(entity, request);
        entity.DataAtualizacao = clock.GetUtcNow().UtcDateTime;
        entity.AtualizadoPorUsuarioId = request.UsuarioResponsavelId;
        await db.SaveChangesAsync(ct);
        return LancamentoMapping.ToDto(entity);
    }

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
                l.Finalidade.ToLower().Contains(term) || categorias.Contains(l.Categoria));
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
