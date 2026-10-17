using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Almirante.Api.Services;

public static partial class FotoChave
{
    public const int Tamanho = 79;
    public const string Prefixo = "usuarios/";
    public const string Extensao = ".webp";

    [GeneratedRegex(@"\Ausuarios/[0-9a-f]{32}/[0-9a-f]{32}\.webp\z", RegexOptions.CultureInvariant)]
    private static partial Regex Formato();

    public static string Gerar(Guid usuarioId) =>
        $"{Prefixo}{usuarioId:N}/{RandomNumberGenerator.GetHexString(32, lowercase: true)}{Extensao}";

    public static bool FormatoValido(string? chave) => chave is { Length: Tamanho } && Formato().IsMatch(chave);

    public static bool PertenceA(string? chave, Guid usuarioId) =>
        FormatoValido(chave) && chave!.AsSpan(Prefixo.Length, 32).SequenceEqual(usuarioId.ToString("N"));

    public static string ETag(string chave) => $"\"{chave.Substring(Prefixo.Length + 33, 32)}\"";
}
