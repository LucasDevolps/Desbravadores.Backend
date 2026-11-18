namespace Almirante.Api.Resources;

/// <summary>
/// Marcador dos textos de notificação em arquivo de recurso (<c>NotificacoesTextos.resx</c>), lidos por
/// <c>IStringLocalizer&lt;NotificacoesTextos&gt;</c>. O recurso neutro é pt-BR (NeutralLanguage do projeto); outros idiomas entram como
/// <c>NotificacoesTextos.{cultura}.resx</c>. Por enquanto só a notificação de devolução de evento (issue #209) usa este padrão.
/// </summary>
public sealed class NotificacoesTextos
{
    public const string DevolucaoEventoTitulo = "DevolucaoEvento_Titulo";
    public const string DevolucaoEventoMensagem = "DevolucaoEvento_Mensagem";
}
