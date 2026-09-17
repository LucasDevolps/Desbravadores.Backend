using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentosService(AlmiranteDbContext db, TimeProvider clock, ILogger<LancamentosService> logger)
{
    public async Task<LancamentosResponse> ListAsync(int page, int pageSize, string? search, string? status,
        string? finalidade, DateOnly? vencimento, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Lancamentos.AsNoTracking().Where(l => l.Ativo);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(l => l.Membro!.Nome.ToLower().Contains(term) ||
                (l.Descricao != null && l.Descricao.ToLower().Contains(term)) ||
                l.Finalidade.ToLower().Contains(term) || l.Categoria.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(status) && status != "Todos") query = query.Where(l => l.Status == status);
        if (!string.IsNullOrWhiteSpace(finalidade) && finalidade != "Todos") query = query.Where(l => l.Finalidade == finalidade);
        if (vencimento.HasValue) query = query.Where(l => l.Vencimento == vencimento);

        var total = await query.CountAsync(ct);
        var items = await Project(query.OrderByDescending(l => l.Vencimento).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
        return new LancamentosResponse { Items = items, Total = total, Page = page, PageSize = pageSize,
            TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) };
    }

    public async Task<RegistrarLancamentoResult> RegistrarAsync(RegistrarLancamentoRequest request, CancellationToken ct)
    {
        if (request.AplicarATodosOsMembros) return await RegistrarTodosAsync(request, ct);

        var membro = await db.Usuarios.AsNoTracking().Where(u => u.Id == request.MembroId).Select(u => new { u.Id, u.Nome }).SingleOrDefaultAsync(ct);
        if (membro is null) return new(RegistrarLancamentoOutcome.MembroNaoEncontrado, null, null);

        var entity = Criar(request, membro.Id, null, clock.GetUtcNow().UtcDateTime);
        db.Lancamentos.Add(entity);
        await db.SaveChangesAsync(ct);
        return new(RegistrarLancamentoOutcome.LancamentoUnicoCriado, ToDto(entity, membro.Nome), null);
    }

    private async Task<RegistrarLancamentoResult> RegistrarTodosAsync(RegistrarLancamentoRequest request, CancellationToken ct)
    {
        var key = request.IdempotencyKey!.Trim();
        var hash = Hash(request);
        var existing = await db.LancamentosOperacoes.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (existing is not null) return Existing(existing, hash);

        var membros = await db.Usuarios.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var operationId = Guid.NewGuid();
        var entities = membros.Select(id => Criar(request, id, operationId, now)).ToList();
        var operation = new LancamentoOperacao
        {
            Id = operationId, IdempotencyKey = key, RequestHash = hash, Finalidade = request.Finalidade,
            Descricao = request.Descricao, Categoria = request.Categoria, TipoFluxo = request.TipoFluxo,
            Valor = request.Valor, Vencimento = request.Vencimento, UsuariosProcessados = membros.Count,
            LancamentosCriados = entities.Count, CriadoPorUsuarioId = request.UsuarioSolicitanteId, CriadoEmUtc = now
        };
        db.LancamentosOperacoes.Add(operation); db.Lancamentos.AddRange(entities);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.LancamentosOperacoes.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (existing is null) throw;
            return Existing(existing, hash);
        }
        return new(RegistrarLancamentoOutcome.GeralCriado, null, Response(operation));
    }

    public async Task<LancamentoDto?> UpdateAsync(Guid id, UpdateLancamentoRequest request, CancellationToken ct)
    {
        var entity = await db.Lancamentos.Include(l => l.Membro).SingleOrDefaultAsync(l => l.Id == id && l.Ativo, ct);
        if (entity is null) return null;
        if (request.Finalidade is not null) entity.Finalidade = request.Finalidade;
        if (request.Descricao is not null) entity.Descricao = request.Descricao;
        if (request.Categoria is not null) entity.Categoria = request.Categoria;
        if (request.TipoFluxo.HasValue) entity.TipoFluxo = request.TipoFluxo.Value;
        if (request.Valor.HasValue) entity.Valor = request.Valor.Value;
        if (request.Vencimento.HasValue) entity.Vencimento = request.Vencimento.Value;
        if (request.Status is not null) entity.Status = request.Status;
        entity.DataAtualizacao = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(entity, entity.Membro!.Nome);
    }

    public async Task<bool> DeleteAsync(DeleteLancamentoRequest request, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var entity = await db.Lancamentos.SingleOrDefaultAsync(l => l.Id == request.Id && l.Ativo, ct);
            if (entity is null) return false;
            entity.Ativo = false; await db.SaveChangesAsync(ct); return true;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={request.UsuarioResponsavelId};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value={request.IpResponsavel};", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value={request.Motivo.Trim()};", ct);
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Lancamentos SET Ativo=0 WHERE Id={request.Id} AND Ativo=1;", ct);
            if (affected == 0) { await tx.RollbackAsync(ct); return false; }
            await tx.CommitAsync(ct); return true;
        }
        finally
        {
            try { await db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL; EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL; EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=NULL;", CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Falha ao limpar SESSION_CONTEXT."); }
        }
    }

    private static Lancamento Criar(RegistrarLancamentoRequest r, Guid membroId, Guid? operationId, DateTime now) => new()
    { Id = Guid.NewGuid(), MembroId = membroId, Finalidade = r.Finalidade, Descricao = r.Descricao,
      Categoria = r.Categoria, TipoFluxo = r.TipoFluxo, Valor = r.Valor, Vencimento = r.Vencimento,
      Status = LancamentoStatuses.Pendente, DataCriacao = now, Ativo = true, OperacaoId = operationId };

    private static IQueryable<LancamentoDto> Project(IQueryable<Lancamento> query) => query.Select(l => new LancamentoDto
    { Id=l.Id, MembroId=l.MembroId, MembroNome=l.Membro!.Nome, Finalidade=l.Finalidade, Descricao=l.Descricao,
      Categoria=l.Categoria, TipoFluxo=l.TipoFluxo, Valor=l.Valor, Vencimento=l.Vencimento, Status=l.Status });
    private static LancamentoDto ToDto(Lancamento l, string nome) => new()
    { Id=l.Id, MembroId=l.MembroId, MembroNome=nome, Finalidade=l.Finalidade, Descricao=l.Descricao,
      Categoria=l.Categoria, TipoFluxo=l.TipoFluxo, Valor=l.Valor, Vencimento=l.Vencimento, Status=l.Status };
    private static string Hash(RegistrarLancamentoRequest r)
    {
        var canonical = string.Join('|', r.Finalidade, r.Descricao?.Trim() ?? "", r.Categoria, r.TipoFluxo,
            r.Valor.ToString("F2", CultureInfo.InvariantCulture), r.Vencimento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    private static RegistrarLancamentoResult Existing(LancamentoOperacao op, string hash) => op.RequestHash == hash
        ? new(RegistrarLancamentoOutcome.GeralReutilizado, null, Response(op))
        : new(RegistrarLancamentoOutcome.GeralConflitoIdempotencia, null, null);
    private static LancamentoGeralResponse Response(LancamentoOperacao op) => new()
    { OperacaoId=op.Id, UsuariosProcessados=op.UsuariosProcessados, LancamentosCriados=op.LancamentosCriados, DataHoraUtc=op.CriadoEmUtc };
}
