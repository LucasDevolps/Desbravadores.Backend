namespace Almirante.Api.Entities;

public sealed class Lancamento
{
    public Guid Id { get; set; }
    public Guid? MembroId { get; set; }
    public Usuario? Membro { get; set; }
    public string? Finalidade { get; set; }
    public string? Descricao { get; set; }
    public required CategoriaLancamento Categoria { get; set; }
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }
    public StatusLancamento Status { get; set; } = StatusLancamento.Pendente;
    public TipoFluxoLancamento TipoFluxo { get; set; }
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;
    public DateTime? DataAtualizacao { get; set; }
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public bool Ativo { get; set; } = true;
    public Guid? OperacaoId { get; set; }

    public Guid? EventoId { get; set; }
}
