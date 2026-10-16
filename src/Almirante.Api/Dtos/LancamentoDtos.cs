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
    // Preenchido quando o lançamento foi gerado por um cadastro de evento (edição só via /api/Eventos).
    public Guid? EventoId { get; init; }
}

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
    // Preenchido pelo controller a partir da identidade autenticada (User.TentarObterUsuarioId);
    // nunca aceito do body — ver LancamentosController.Update.
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
