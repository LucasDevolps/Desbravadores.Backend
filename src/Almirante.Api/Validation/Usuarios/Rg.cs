namespace Almirante.Api.Validation.Usuarios;

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
