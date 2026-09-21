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
