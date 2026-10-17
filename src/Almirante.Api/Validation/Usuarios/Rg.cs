namespace Almirante.Api.Validation.Usuarios;

public static class Rg
{
    public const int Maximo = 20;

    public const string ObrigatorioSemCin =
        "rg é obrigatório quando ehCin é false; com ehCin true (documento é a CIN, que usa o CPF) o rg é opcional.";

    /// <summary>
    /// Regra do RG no PUT, sobre os valores efetivos (corpo ou gravados): sem CIN o RG é exigido de quem já cumpria a regra (tinha RG
    /// ou era CIN) e de quem informa rg ou ehCin no corpo; como no CPF, o cadastro antigo sem RG e sem CIN que não mexe nesses campos
    /// continua editável.
    /// </summary>
    public static bool FaltaNoPut(bool ehCin, string? rg, bool ehCinGravado, string? rgGravado, bool documentoInformado) =>
        !ehCin && rg is null && (rgGravado is not null || ehCinGravado || documentoInformado);

    public static string? Normalizar(string? rg)
    {
        var valor = rg?.Trim();
        return string.IsNullOrEmpty(valor) ? null : valor;
    }

    public static bool EhValido(string? rgNormalizado) =>
        rgNormalizado is { Length: > 0 and <= Maximo }
        && rgNormalizado.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '/' or ' ')
        && rgNormalizado.Any(char.IsAsciiDigit);
}
