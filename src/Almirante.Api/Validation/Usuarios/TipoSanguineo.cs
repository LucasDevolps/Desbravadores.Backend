namespace Almirante.Api.Validation.Usuarios;

public static class TipoSanguineo
{
    public const int Maximo = 3;

    public static readonly string[] Valores = ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"];

    public static string? Normalizar(string? tipo)
    {
        var valor = tipo?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(valor) ? null : valor;
    }

    public static bool EhValido(string? tipoNormalizado) => tipoNormalizado is not null && Valores.Contains(tipoNormalizado, StringComparer.Ordinal);
}
