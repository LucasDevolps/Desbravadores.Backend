using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

public static class LancamentoFinalidades
{
    public const string Mensalidade = "Mensalidade";
    public const string Campori = "Campori";
    public const string Acampamento = "Acampamento";
    public const string Uniflash = "Uniflash";
    public const string Doacao = "Doação";
    public const string Evento = "Evento";
    public const string Outros = "Outros";
    public static readonly IReadOnlyCollection<string> Todas = [Mensalidade, Campori, Acampamento, Uniflash, Doacao, Evento, Outros];
}

public static class LancamentoCategorias
{
    public const string Clube = "Clube";
    public const string Evento = "Evento";
    public static readonly IReadOnlyCollection<string> Todas = [Clube, Evento];
}

public static class LancamentoStatuses
{
    public const string Pago = "Pago";
    public const string Pendente = "Pendente";
    public const string Atrasado = "Atrasado";
    public static readonly IReadOnlyCollection<string> Todos = [Pago, Pendente, Atrasado];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TipoFluxoLancamento { Entrada, Despesa }

public sealed class Lancamento
{
    public Guid Id { get; set; }
    public Guid? MembroId { get; set; }
    public Usuario? Membro { get; set; }
    public required string Finalidade { get; set; }
    public string? Descricao { get; set; }
    public required string Categoria { get; set; }
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }
    public string Status { get; set; } = LancamentoStatuses.Pendente;
    public TipoFluxoLancamento TipoFluxo { get; set; }
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;
    public DateTime? DataAtualizacao { get; set; }
    // Nullable porque lançamentos históricos foram atualizados antes desta coluna existir (issue #50).
    // Vem exclusivamente da identidade autenticada (ver LancamentosController.Update); nunca do body da requisição.
    public Guid? AtualizadoPorUsuarioId { get; set; }
    public bool Ativo { get; set; } = true;
    public Guid? OperacaoId { get; set; }
}
