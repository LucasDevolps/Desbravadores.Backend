using System.Buffers.Text;
using System.Security.Cryptography;

namespace Almirante.Api.Security;

/// <summary>
/// Token da autorização de recuperação: 32 bytes aleatórios do gerador criptográfico em Base64Url (43 caracteres), válido por 15 minutos,
/// de uso único e ligado ao titular e à SecurityVersion dele. Só o SHA-256 com prefixo de finalidade é gravado, de modo que o mesmo valor
/// não serviria como refresh token nem para outro propósito. Não é JWT e não autoriza nenhuma outra operação.
/// </summary>
public static class TokensRecuperacaoSenha
{
    public const int Bytes = 32;
    public const int Caracteres = 43;
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(15);

    private static readonly byte[] Finalidade = "almirante:recuperacao-senha:v1\0"u8.ToArray();

    public static string Gerar(out byte[] hash)
    {
        var bytes = RandomNumberGenerator.GetBytes(Bytes);
        hash = Hash(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Falso para qualquer valor que não seja um token no formato emitido (a resposta ao cliente é a mesma de um token inválido).</summary>
    public static bool TentarCalcularHash(string? token, out byte[] hash)
    {
        hash = [];
        if (token is null || token.Length != Caracteres || !Base64Url.IsValid(token, out var tamanho) || tamanho != Bytes)
        {
            return false;
        }

        hash = Hash(Base64Url.DecodeFromChars(token));
        return true;
    }

    private static byte[] Hash(byte[] bytes) => SHA256.HashData([.. Finalidade, .. bytes]);
}
