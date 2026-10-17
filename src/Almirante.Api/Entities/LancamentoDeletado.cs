namespace Almirante.Api.Entities;

public sealed class LancamentoDeletado
{
    public Guid Id { get; set; }
    public Guid LancamentoId { get; set; }

    public Guid UsuarioResponsavelId { get; set; }

    public required string IpResponsavel { get; set; }

    public DateTime ExcluidoEmUtc { get; set; }

    public required string Motivo { get; set; }

    public Guid? MembroId { get; set; }
    public string? Finalidade { get; set; }
    public required CategoriaLancamento Categoria { get; set; }
    public required TipoFluxoLancamento TipoFluxo { get; set; }
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }
    public required StatusLancamento Status { get; set; }
    public Guid? OperacaoId { get; set; }
    public DateTime DataCriacaoOriginal { get; set; }
    public Guid? EventoId { get; set; }
}
