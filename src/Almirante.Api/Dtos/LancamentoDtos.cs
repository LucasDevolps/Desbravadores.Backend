using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using MediatR;

namespace Almirante.Api.Dtos;

public sealed record LancamentoDto
{
    public Guid Id { get; init; }
    public Guid? MembroId { get; init; }
    public required string MembroNome { get; init; }
    public string? Finalidade { get; init; }
    public string? Descricao { get; init; }
    public required CategoriaLancamento Categoria { get; init; }
    public TipoFluxoLancamento TipoFluxo { get; init; }
    public decimal Valor { get; init; }
    public DateOnly Vencimento { get; init; }
    public required StatusLancamento Status { get; init; }
    public Guid? EventoId { get; init; }

    /// <summary>Data efetiva da liquidação; nula sem pagamento registrado.</summary>
    public DateOnly? DataPagamento { get; init; }

    /// <summary>Valor efetivamente pago/recebido; nulo sem pagamento registrado.</summary>
    public decimal? ValorPago { get; init; }

    /// <summary>Calculado: valor − valorPago com pagamento registrado; valor sem pagamento; nulo em "Pago" histórico sem dados.</summary>
    public decimal? ValorRestante { get; init; }

    public Guid? PagamentoRegistradoPorUsuarioId { get; init; }
    public DateTime? PagamentoRegistradoEmUtc { get; init; }
}

/// <summary>Corpo de POST/PUT /api/Lancamentos/{id}/Pagamento.</summary>
public abstract class PagamentoLancamentoRequest : IRequest<LancamentoDto?>
{
    /// <summary>Data efetiva do pagamento/recebimento (yyyy-MM-dd), até hoje no fuso do clube.</summary>
    public DateOnly? DataPagamento { get; set; }

    /// <summary>Valor efetivamente pago/recebido: maior que zero, até 2 casas decimais e no máximo o valor do lançamento.</summary>
    public decimal? ValorPago { get; set; }

    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
}

/// <summary>Registra o pagamento de um lançamento Pendente, Atrasado ou "Pago" histórico sem dados de liquidação.</summary>
public sealed class RegistrarPagamentoRequest : PagamentoLancamentoRequest;

/// <summary>Corrige data e/ou valor de um pagamento já registrado.</summary>
public sealed class AtualizarPagamentoRequest : PagamentoLancamentoRequest;

public sealed record LancamentosResponse
{
    public required IReadOnlyList<LancamentoDto> Items { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }
}

public sealed record GetLancamentoQuery(Guid Id) : IRequest<LancamentoDto?>;

public sealed record ListLancamentosQuery(int Page, int PageSize, string? Search, string? Status,
    string? Finalidade, DateOnly? Vencimento) : IRequest<LancamentosResponse>;

public enum RegistrarLancamentoOutcome { LancamentoUnicoCriado, GeralCriado, GeralReutilizado, GeralConflitoIdempotencia, MembroNaoEncontrado }

public sealed record RegistrarLancamentoResult(RegistrarLancamentoOutcome Outcome, LancamentoDto? Lancamento,
    LancamentoGeralResponse? Geral);

/// <summary>Contrato canônico para lançamentos individuais e em lote.</summary>
public sealed class RegistrarLancamentoRequest : IRequest<RegistrarLancamentoResult>
{
    [JsonIgnore] public string? IdempotencyKey { get; set; }
    [JsonIgnore] public Guid UsuarioSolicitanteId { get; set; }
    public Guid? MembroId { get; set; }
    public required string Finalidade { get; set; }
    public string? Descricao { get; set; }
    public required CategoriaLancamento Categoria { get; set; }
    public TipoFluxoLancamento TipoFluxo { get; set; }
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }
    public bool AplicarATodosOsMembros { get; set; }
}

public sealed record LancamentoGeralResponse
{
    public Guid OperacaoId { get; init; }
    public int UsuariosProcessados { get; init; }
    public int LancamentosCriados { get; init; }
    public DateTime DataHoraUtc { get; init; }
}

public sealed class UpdateLancamentoRequest : IRequest<LancamentoDto?>
{
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
    public string? Finalidade { get; set; }
    public string? Descricao { get; set; }
    public CategoriaLancamento? Categoria { get; set; }
    public TipoFluxoLancamento? TipoFluxo { get; set; }
    public decimal? Valor { get; set; }
    public DateOnly? Vencimento { get; set; }
    public StatusLancamento? Status { get; set; }
}

public sealed class DeleteLancamentoRequest : IRequest<bool>
{
    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
    public required string Motivo { get; set; }
}
