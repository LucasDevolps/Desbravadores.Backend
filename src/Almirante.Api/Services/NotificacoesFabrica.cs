using System.Globalization;
using Almirante.Api.Entities;
using Almirante.Api.Validation.Usuarios;

namespace Almirante.Api.Services;

/// <summary>
/// Monta as notificações dos gatilhos de cadastro. Só cria entidades: quem chama as adiciona ao mesmo DbContext da operação
/// de negócio, para que notificação e operação sejam gravadas na mesma transação.
/// </summary>
public static class NotificacoesFabrica
{
    public const int TituloMaximo = 200;
    public const int MensagemMaximo = 1000;
    public const int ChaveOrigemMaximo = 100;

    /// <summary>
    /// Débito do membro: lançamento de Entrada (o clube recebe) atribuído a ele, como as cobranças de evento e as mensalidades.
    /// Saída é despesa do clube (nome legado "Despesa"); mesmo com membro, não é cobrança e não notifica.
    /// </summary>
    public static bool EhDebito(Lancamento lancamento) =>
        lancamento.MembroId is not null && lancamento.TipoFluxo == TipoFluxoLancamento.Entrada;

    /// <summary>Destinatários de um cadastro de débito: uma notificação por lançamento de débito, para o membro dele.</summary>
    public static List<Notificacao> ParaDebitos(IEnumerable<Lancamento> lancamentos, DateTime agoraUtc) =>
        lancamentos.Where(EhDebito).Select(l => DebitoCadastrado(l, agoraUtc)).ToList();

    /// <summary>
    /// Destinatários de um cadastro de evento: os participantes deste cadastro, uma notificação cada, a partir do lançamento
    /// gerado para ele. Os lançamentos do evento não passam por <see cref="ParaDebitos"/>.
    /// </summary>
    public static List<Notificacao> ParaEvento(Evento evento, IEnumerable<Lancamento> lancamentosDoCadastro, DateTime agoraUtc) =>
        lancamentosDoCadastro.Where(l => l.MembroId is not null && l.EventoId == evento.Id)
            .GroupBy(l => l.MembroId!.Value)
            .Select(g => EventoCadastrado(evento, g.First(), agoraUtc))
            .ToList();

    public static string ChaveDebito(Guid lancamentoId) => $"lancamento:{lancamentoId:N}";

    public static string ChaveEvento(Guid eventoId) => $"evento:{eventoId:N}";

    public static Notificacao DebitoCadastrado(Lancamento lancamento, DateTime agoraUtc)
    {
        var descricao = string.IsNullOrWhiteSpace(lancamento.Descricao) ? "" : $" ({lancamento.Descricao.Trim()})";
        return Nova(lancamento.MembroId!.Value, TipoNotificacao.DebitoCadastrado, ChaveDebito(lancamento.Id), agoraUtc,
            $"Novo débito: {lancamento.Finalidade}",
            $"Foi registrado um débito de {Moeda(lancamento.Valor)} referente a {lancamento.Finalidade}{descricao}, " +
            $"com vencimento em {Data(lancamento.Vencimento)}.",
            eventoId: null, lancamento.Id);
    }

    /// <summary>Uma notificação por participante, já com o valor do lançamento do evento (não há notificação de débito à parte).</summary>
    public static Notificacao EventoCadastrado(Evento evento, Lancamento lancamento, DateTime agoraUtc)
    {
        var valor = lancamento.Valor > 0
            ? $" Valor a pagar: {Moeda(lancamento.Valor)}, com vencimento em {Data(lancamento.Vencimento)}."
            : " Não há valor a pagar por este evento.";
        return Nova(lancamento.MembroId!.Value, TipoNotificacao.EventoCadastrado, ChaveEvento(evento.Id), agoraUtc,
            $"Novo evento: {evento.Local}",
            $"Você está na lista de participantes do evento em {evento.Local}, no dia {Data(evento.DataEvento)}.{valor}",
            evento.Id, lancamento.Id);
    }

    public static string Moeda(decimal valor)
    {
        var texto = Math.Abs(valor).ToString("#,##0.00", CultureInfo.InvariantCulture);
        var brasileiro = string.Concat(texto.Select(c => c switch { ',' => '.', '.' => ',', _ => c }));
        return (valor < 0 ? "-R$ " : "R$ ") + brasileiro;
    }

    private static string Data(DateOnly data) => data.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static Notificacao Nova(Guid destinatarioId, TipoNotificacao tipo, string chaveOrigem, DateTime agoraUtc,
        string titulo, string mensagem, Guid? eventoId, Guid? lancamentoId) => new()
    {
        Id = Guid.NewGuid(),
        DestinatarioId = destinatarioId,
        Tipo = tipo,
        Titulo = Ajustar(titulo, TituloMaximo),
        Mensagem = Ajustar(mensagem, MensagemMaximo),
        CriadaEmUtc = agoraUtc,
        EventoId = eventoId,
        LancamentoId = lancamentoId,
        ChaveOrigem = chaveOrigem,
        AvisoPendente = AvisoNotificacao.Criada,
        AvisoTentativas = 0,
        AvisoProximaTentativaEmUtc = agoraUtc,
    };

    private static string Ajustar(string texto, int maximo)
    {
        var compativel = TextoVarchar.ParaPagina1252(texto);
        return compativel.Length <= maximo ? compativel : compativel[..(maximo - 1)].TrimEnd() + "…";
    }
}
