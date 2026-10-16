namespace Almirante.Api.Validation.Usuarios;

// Tipo sanguíneo ABO/Rh. Opcional: quem não sabe deixa vazio (NULL), nunca um valor padrão. A API aceita maiúsculas ou
// minúsculas e grava sempre em maiúsculas, o mesmo conjunto do CHECK CK_Usuarios_TipoSanguineo.
public static class TipoSanguineo
{
    public const int Maximo = 3;

    public static readonly string[] Valores = ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"];

    // Sem espaços externos e em maiúsculas; vazio vira NULL. Não valida (ver EhValido).
    public static string? Normalizar(string? tipo)
    {
        var valor = tipo?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(valor) ? null : valor;
    }

    public static bool EhValido(string? tipoNormalizado) => tipoNormalizado is not null && Valores.Contains(tipoNormalizado, StringComparer.Ordinal);
}
