using System.Globalization;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public class LancamentoValidationException(string message) : Exception(message);

public class LancamentosService(AlmiranteDbContext db)
{
    private const string DateFormat = LancamentoValidacao.DateFormat;

    public async Task<LancamentosResponse> ListAsync(
        int page,
        int pageSize,
        string? search,
        string? status,
        string? tipo,
        string? data,
        CancellationToken cancellationToken)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 10 : Math.Min(pageSize, 100);

        var query = db.Lancamentos.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower(CultureInfo.InvariantCulture);
            query = query.Where(l =>
                l.MembroNome.ToLower().Contains(term) ||
                (l.Descricao != null && l.Descricao.ToLower().Contains(term)) ||
                l.Tipo.ToLower().Contains(term) ||
                l.Categoria.ToLower().Contains(term) ||
                l.Status.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(status) && status != "Todos")
        {
            query = query.Where(l => l.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(tipo) && tipo != "Todos")
        {
            query = query.Where(l => l.Tipo == tipo);
        }

        if (!string.IsNullOrWhiteSpace(data) &&
            DateOnly.TryParseExact(data, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dataFiltro))
        {
            query = query.Where(l => l.Vencimento == dataFiltro);
        }

        var total = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));

        var items = await query
            .OrderByDescending(l => l.Vencimento)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new LancamentosResponse
        {
            Items = items.Select(ToDto).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
        };
    }

    public async Task<LancamentoDto> CreateAsync(CreateLancamentoRequest request, CancellationToken cancellationToken)
    {
        LancamentoValidacao.ValidateTipo(request.Tipo);
        LancamentoValidacao.ValidateCategoria(request.Categoria);
        LancamentoValidacao.ValidateStatus(request.Status);
        LancamentoValidacao.ValidateTipoFluxo(request.TipoFluxo);
        LancamentoValidacao.ValidateValor(request.Valor);
        var vencimento = LancamentoValidacao.ParseVencimento(request.Vencimento);
        LancamentoValidacao.ValidateVencimentoNaoPassado(vencimento);

        var lancamento = new Lancamento
        {
            Id = Guid.NewGuid(),
            MembroId = request.MembroId,
            MembroNome = request.MembroNome,
            Tipo = request.Tipo,
            Descricao = request.Descricao,
            Categoria = request.Categoria,
            TipoFluxo = request.TipoFluxo,
            Valor = request.Valor,
            Moeda = string.IsNullOrWhiteSpace(request.Moeda) ? "BRL" : request.Moeda,
            Vencimento = vencimento,
            Status = request.Status,
            DataCriacao = DateTime.UtcNow,
        };

        db.Lancamentos.Add(lancamento);
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(lancamento);
    }

    public async Task<LancamentoDto?> UpdateAsync(Guid id, UpdateLancamentoRequest request, CancellationToken cancellationToken)
    {
        var lancamento = await db.Lancamentos.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (lancamento is null)
        {
            return null;
        }

        if (request.MembroId.HasValue)
        {
            lancamento.MembroId = request.MembroId;
        }

        if (request.MembroNome is not null)
        {
            lancamento.MembroNome = request.MembroNome;
        }

        if (request.Tipo is not null)
        {
            LancamentoValidacao.ValidateTipo(request.Tipo);
            lancamento.Tipo = request.Tipo;
        }

        if (request.Descricao is not null)
        {
            lancamento.Descricao = request.Descricao;
        }

        if (request.Categoria is not null)
        {
            LancamentoValidacao.ValidateCategoria(request.Categoria);
            lancamento.Categoria = request.Categoria;
        }

        if (request.TipoFluxo is not null)
        {
            LancamentoValidacao.ValidateTipoFluxo(request.TipoFluxo);
            lancamento.TipoFluxo = request.TipoFluxo;
        }

        if (request.Valor.HasValue)
        {
            LancamentoValidacao.ValidateValor(request.Valor.Value);
            lancamento.Valor = request.Valor.Value;
        }

        if (request.Moeda is not null)
        {
            lancamento.Moeda = request.Moeda;
        }

        if (request.Vencimento is not null)
        {
            var novoVencimento = LancamentoValidacao.ParseVencimento(request.Vencimento);
            LancamentoValidacao.ValidateVencimentoNaoPassado(novoVencimento);
            lancamento.Vencimento = novoVencimento;
        }

        if (request.Status is not null)
        {
            LancamentoValidacao.ValidateStatus(request.Status);
            lancamento.Status = request.Status;
        }

        lancamento.DataAtualizacao = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(lancamento);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var lancamento = await db.Lancamentos.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (lancamento is null)
        {
            return false;
        }

        db.Lancamentos.Remove(lancamento);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static LancamentoDto ToDto(Lancamento lancamento) => new()
    {
        Id = lancamento.Id,
        MembroId = lancamento.MembroId,
        MembroNome = lancamento.MembroNome,
        Tipo = lancamento.Tipo,
        Descricao = lancamento.Descricao,
        Categoria = lancamento.Categoria,
        Valor = lancamento.Valor,
        Moeda = lancamento.Moeda,
        Vencimento = lancamento.Vencimento.ToString(DateFormat, CultureInfo.InvariantCulture),
        Status = lancamento.Status,
        TipoFluxo = lancamento.TipoFluxo,
    };
}
