namespace Almirante.Api.Validation.Usuarios;

// Telefone brasileiro com DDD (issue #112), gravado só com dígitos: a máscara "(11) 98765-4321" é apresentação.
// Fixo: 10 dígitos, número começando de 2 a 5. Celular: 11 dígitos, começando com 9. DDD da lista de códigos de área
// da Anatel (ex.: 23, 25, 26 e 29 não existem).
public static class TelefoneBrasileiro
{
    public const int MaximoDigitos = 11;

    private static readonly HashSet<int> Ddds =
    [
        11, 12, 13, 14, 15, 16, 17, 18, 19, 21, 22, 24, 27, 28, 31, 32, 33, 34, 35, 37, 38,
        41, 42, 43, 44, 45, 46, 47, 48, 49, 51, 53, 54, 55, 61, 62, 63, 64, 65, 66, 67, 68, 69,
        71, 73, 74, 75, 77, 79, 81, 82, 83, 84, 85, 86, 87, 88, 89, 91, 92, 93, 94, 95, 96, 97, 98, 99,
    ];

    // Remove espaços externos e a máscara convencional (parênteses, hífen e espaços); vazio vira NULL. Qualquer outro
    // caractere (letra, "+", hífen Unicode) continua no valor e reprova EhValido.
    public static string? Normalizar(string? telefone)
    {
        if (telefone is null)
        {
            return null;
        }

        var digitos = string.Concat(telefone.Trim().Where(c => c is not ('(' or ')' or '-' or ' ')));
        return digitos.Length == 0 ? null : digitos;
    }

    public static bool EhValido(string? digitos)
    {
        if (digitos is not { Length: 10 or 11 } || !digitos.All(char.IsAsciiDigit))
        {
            return false;
        }

        var ddd = (digitos[0] - '0') * 10 + (digitos[1] - '0');
        var primeiro = digitos[2];
        return Ddds.Contains(ddd) && (digitos.Length == 11 ? primeiro == '9' : primeiro is >= '2' and <= '5');
    }
}
