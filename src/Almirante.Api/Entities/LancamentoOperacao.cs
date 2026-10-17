namespace Almirante.Api.Entities;

public sealed class LancamentoOperacao
{
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }

    public required string RequestHash { get; set; }

    public required string Finalidade { get; set; }
    public string? Descricao { get; set; }
    public required CategoriaLancamento Categoria { get; set; }
    public required TipoFluxoLancamento TipoFluxo { get; set; }
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }

    public int UsuariosProcessados { get; set; }
    public int LancamentosCriados { get; set; }

    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; } = DateTime.UtcNow;
}
