namespace Almirante.Api.Entities;

public static class LancamentoTipos
{
    public const string Mensalidade = "Mensalidade";
    public const string Campori = "Campori";
    public const string Acampamento = "Acampamento";
    public const string Uniflash = "Uniflash";
    public const string Doacao = "Doação";
    public const string Evento = "Evento";
    public const string Outros = "Outros";

    public static readonly IReadOnlyCollection<string> Todos =
    [
        Mensalidade, Campori, Acampamento, Uniflash, Doacao, Evento, Outros
    ];
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

public class Lancamento
{
    public Guid Id { get; set; }
    public Guid? MembroId { get; set; }
    public required string MembroNome { get; set; }
    public required string Tipo { get; set; }
    public string? Descricao { get; set; }
    public required string Categoria { get; set; }
    public decimal Valor { get; set; }
    public string Moeda { get; set; } = "BRL";
    public DateOnly Vencimento { get; set; }
    public required string Status { get; set; }
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;
    public DateTime? DataAtualizacao { get; set; }

    // Exclusão lógica: nunca há DELETE físico para lançamentos do escopo "geral" (ver
    // LancamentosGeraisService). Um trigger no banco audita toda transição true -> false desta
    // coluna na tabela lancamentos_deletados (ver migration AddLancamentoGeral).
    public bool Ativo { get; set; } = true;

    // Não nulo somente para lançamentos criados pelo endpoint de lançamento geral
    // (LancamentosGeraisController). Lançamentos criados pelo CRUD genérico (LancamentosController)
    // continuam com OperacaoId nulo e ficam fora do escopo/listagem do lançamento geral.
    public Guid? OperacaoId { get; set; }
}
