using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Almirante.Api.Services;

// Chave (caminho RELATIVO à pasta Media:Storage:Path) da foto de um usuário: usuarios/{Id:N}/{aleatório}.webp, sempre
// gerada pelo servidor (o nome enviado pelo cliente nunca é usado). Mesmo formato exigido pela CHECK
// CK_Usuarios_FotoCaminho (AlmiranteDbContext.CheckFotoCaminho): 79 caracteres, hex minúsculo, sem "..", barra invertida
// ou caminho absoluto. A parte aleatória (128 bits de CSPRNG) muda a cada envio e serve de ETag.
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

    // Formato válido E pasta do próprio usuário: um caminho gravado por outra via não aponta para a foto de outro.
    public static bool PertenceA(string? chave, Guid usuarioId) =>
        FormatoValido(chave) && chave!.AsSpan(Prefixo.Length, 32).SequenceEqual(usuarioId.ToString("N"));

    public static string ETag(string chave) => $"\"{chave.Substring(Prefixo.Length + 33, 32)}\"";
}
