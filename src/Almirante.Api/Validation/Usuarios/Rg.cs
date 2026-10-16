namespace Almirante.Api.Validation.Usuarios;

// RG: opcional e sem dígito verificador nacional (cada estado tem o seu formato, alguns com letras ou "X" no fim), então
// só o formato geral é verificado: letras sem acento, dígitos, ponto, hífen, barra e espaço, com pelo menos um dígito.
// Gravado como informado, sem espaços externos; não é único (números de estados diferentes podem coincidir).
public static class Rg
{
    public const int Maximo = 20;

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
