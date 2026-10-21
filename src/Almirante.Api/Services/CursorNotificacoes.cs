using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Almirante.Api.Services;

/// <summary>
/// Cursor opaco da listagem de pendências: a última posição entregue (criadaEmUtc, id). Paginação por chave, e não por
/// deslocamento, para que marcar notificações como lidas entre uma página e outra não faça o cliente pular itens.
/// </summary>
public static class CursorNotificacoes
{
    public const int TamanhoMaximo = 100;

    public static string Criar(DateTime criadaEmUtc, Guid id) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(
            criadaEmUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "." + id.ToString("N")));

    public static bool TentarLer(string? cursor, out DateTime criadaEmUtc, out Guid id)
    {
        criadaEmUtc = default;
        id = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > TamanhoMaximo || !Base64Url.IsValid(cursor))
        {
            return false;
        }

        var partes = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(cursor)).Split('.');
        if (partes.Length != 2
            || !long.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(partes[1], "N", out id))
        {
            return false;
        }

        criadaEmUtc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}
