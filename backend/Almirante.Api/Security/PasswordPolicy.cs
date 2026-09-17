namespace Almirante.Api.Security;

// Política aplicada ao DEFINIR uma senha (hoje: bootstrap do admin em DbSeeder). O login não usa
// esta política: credenciais existentes continuam sendo verificadas pelo hash mesmo que deixem de
// cumprir regras novas.
//
// Limites do hash: PasswordHasher<T> (formato V3) usa PBKDF2-HMAC-SHA512 sobre a senha inteira em
// UTF-8, sem truncamento (diferente do bcrypt, que ignora além de 72 bytes). O máximo de 128
// caracteres existe só para limitar custo/abuso, e é validado explicitamente — nunca se corta a
// senha silenciosamente.
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    // Senhas/raízes triviais conhecidas (inclusive as que já estiveram versionadas neste repositório).
    private static readonly HashSet<string> TrivialRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "senha", "password", "passw0rd", "admin", "administrador", "administrator", "almirante",
        "desbravador", "desbravadores", "qwerty", "qwertyuiop", "letmein", "welcome", "bemvindo",
        "iloveyou", "abc", "abcdef", "teste", "test", "root", "usuario", "user", "mudar", "changeme",
    };

    public static IReadOnlyList<string> Validate(string? password, string? email = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(password))
        {
            errors.Add("A senha é obrigatória.");
            return errors;
        }

        if (password.Length < MinLength) errors.Add($"A senha deve ter ao menos {MinLength} caracteres.");
        if (password.Length > MaxLength) errors.Add($"A senha deve ter no máximo {MaxLength} caracteres.");
        if (SecretPlaceholders.IsPlaceholder(password)) errors.Add("A senha ainda é um placeholder.");
        if (password.Distinct().Count() <= 2) errors.Add("A senha não pode ser formada por um ou dois caracteres repetidos.");
        if (IsSequential(password)) errors.Add("A senha não pode ser uma sequência simples (ex.: 123456789012, abcdefghijkl).");

        // "Senha@2026!!" e "admin1234567" reduzem-se a uma raiz trivial após remover dígitos/símbolos.
        var root = new string(password.Where(char.IsLetter).ToArray());
        if (root.Length == 0 || TrivialRoots.Contains(root)) errors.Add("A senha é trivial ou muito comum.");

        var localPart = email?.Split('@')[0];
        if (!string.IsNullOrWhiteSpace(localPart) && localPart.Length >= 4 &&
            password.Contains(localPart, StringComparison.OrdinalIgnoreCase))
            errors.Add("A senha não pode conter o e-mail do usuário.");

        return errors;
    }

    private static bool IsSequential(string value)
    {
        if (value.Length < 3) return false;
        var step = value[1] - value[0];
        if (Math.Abs(step) != 1) return false;
        for (var i = 2; i < value.Length; i++)
        {
            if (value[i] - value[i - 1] != step) return false;
        }
        return true;
    }
}
