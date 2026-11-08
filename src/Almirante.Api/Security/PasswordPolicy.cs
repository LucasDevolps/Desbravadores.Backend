using System.Globalization;
using System.Text;

namespace Almirante.Api.Security;

/// <summary>
/// Política única para toda senha nova: cadastro, seed do administrador, CLI de emergência, troca própria, troca administrativa e
/// conclusão de recuperação (issue #159). Segue o NIST SP 800-63B-4 para senha como único fator: 15 a 128 caracteres contados em pontos
/// de código Unicode, espaços e frases-senha aceitos, sem exigência de mistura de maiúsculas, números ou símbolos, e bloqueio de valores
/// comuns ou vazados por lista local (<see cref="SenhasComuns"/>; nenhuma senha sai do processo). A senha é usada exatamente como digitada:
/// sem trim, truncamento, troca de caixa ou normalização Unicode, para que login, cadastro e trocas continuem compatíveis com os hashes
/// existentes. Senhas antigas não precisam atender a esta política para autenticar; ela só vale ao estabelecer uma senha nova.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 15;
    public const int MaxLength = 128;

    /// <summary>
    /// Iterações do PBKDF2-HMAC-SHA512 (formato V3 do PasswordHasher do ASP.NET Core Identity), conforme o OWASP Password Storage Cheat
    /// Sheet para PBKDF2-HMAC-SHA512. O padrão do framework é 100.000; hashes com menos iterações continuam válidos e são regravados com
    /// este custo no próximo login bem-sucedido.
    /// </summary>
    public const int IteracoesHash = 210_000;

    private static readonly HashSet<string> TrivialRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "senha", "password", "passw0rd", "admin", "administrador", "administrator", "almirante",
        "desbravador", "desbravadores", "qwerty", "qwertyuiop", "letmein", "welcome", "bemvindo",
        "iloveyou", "abc", "abcdef", "teste", "test", "root", "usuario", "user", "mudar", "changeme",
    };

    /// <summary>Tamanho em pontos de código Unicode (um emoji ou ideograma fora do BMP conta como um caractere).</summary>
    public static int Comprimento(string senha) => senha.EnumerateRunes().Count();

    public static IReadOnlyList<string> Validate(string? password, string? email = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(password))
        {
            errors.Add("A senha é obrigatória.");
            return errors;
        }

        var comprimento = Comprimento(password);
        if (comprimento < MinLength) errors.Add($"A senha deve ter ao menos {MinLength} caracteres.");
        if (comprimento > MaxLength) errors.Add($"A senha deve ter no máximo {MaxLength} caracteres.");
        if (SecretPlaceholders.IsPlaceholder(password)) errors.Add("A senha ainda é um placeholder.");
        if (password.EnumerateRunes().Distinct().Count() <= 2) errors.Add("A senha não pode ser formada por um ou dois caracteres repetidos.");
        if (IsSequential(password)) errors.Add("A senha não pode ser uma sequência simples (ex.: 123456789012345, abcdefghijklmno).");
        if (EhRepeticao(password)) errors.Add("A senha não pode ser um mesmo trecho repetido (ex.: azul42azul42azul42).");

        var root = new string(password.Where(char.IsLetter).ToArray());
        if (root.Length == 0 || TrivialRoots.Contains(root) || SenhasComuns.Contem(password))
            errors.Add("A senha é trivial, muito comum ou aparece em listas de senhas vazadas.");

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

    /// <summary>A senha inteira é um bloco repetido duas ou mais vezes (abcabcabcabcabc, senha123senha123), sem diferenciar caixa.</summary>
    private static bool EhRepeticao(string value)
    {
        var texto = value.ToLowerInvariant();
        for (var bloco = 1; bloco <= texto.Length / 2; bloco++)
        {
            if (texto.Length % bloco != 0) continue;
            var repetido = true;
            for (var i = bloco; i < texto.Length && repetido; i++)
            {
                repetido = texto[i] == texto[i - bloco];
            }
            if (repetido) return true;
        }
        return false;
    }
}

/// <summary>
/// Lista local de senhas comuns e vazadas com 15 caracteres ou mais (as mais curtas já são recusadas pelo tamanho), revisada à mão a partir
/// das listas públicas de senhas mais usadas e vazadas e de termos do próprio contexto do clube (igreja, desbravadores, nome do sistema).
/// A comparação ignora caixa, acentos, espaços, pontuação e um sufixo só de números ou símbolos (ex.: "Deus é fiel 2026!" bate com
/// "deusefiel"). Nada é enviado a serviços externos. Atualizações vão por PR, com revisão, como qualquer regra de segurança.
/// </summary>
public static class SenhasComuns
{
    private static readonly HashSet<string> Valores = new(StringComparer.Ordinal)
    {
        // Sequências e padrões de teclado.
        "123456789012345", "1234567890123456", "12345678901234567890", "098765432109876", "qwertyuiopasdfgh",
        "qwertyuiopasdfghjkl", "qwertyuiopasdfghjklzxcvbnm", "asdfghjklzxcvbnm", "zxcvbnmasdfghjkl", "1q2w3e4r5t6y7u8i",
        "1q2w3e4r5t6y7u8i9o0p", "1qaz2wsx3edc4rfv", "1qaz2wsx3edc4rfv5tgb", "zaq12wsxcde34rfv", "qazwsxedcrfvtgbyhn",
        "1qazxsw23edcvfr4", "q1w2e3r4t5y6u7i8", "a1b2c3d4e5f6g7h8", "abcdefghijklmnop", "abcdefghijklmnopqrstuvwxyz",
        "qweasdzxcqweasdzxc", "poiuytrewqlkjhgfdsa", "mnbvcxzlkjhgfdsa",
        // Senhas genéricas longas frequentes em vazamentos.
        "passwordpassword", "password12345678", "password123456789", "mypasswordissecure", "thisismypassword",
        "iloveyouiloveyou", "iloveyouforever", "trustno1trustno1", "letmeinletmein", "welcomewelcome", "changemechangeme",
        "administrator123", "administrador123", "correcthorsebatterystaple", "thequickbrownfoxjumpsoverthelazydog",
        "superman1234567", "batman123456789", "starwars12345678", "pokemon123456789", "football123456789",
        "baseball123456789", "princess12345678", "sunshine12345678", "dragon1234567890", "monkey1234567890",
        "masterkey123456", "qwerty123456789", "qwertyqwertyqwerty", "abc123abc123abc123", "liverpoolfc12345",
        // Português.
        "senhasenhasenha", "minhasenhasecreta", "minhasenhaforte", "minhanovasenha", "minhasenhanova", "minhasenha123456",
        "trocarsenha12345", "senhaforte123456", "senhaprovisoria", "senhatemporaria", "novasenha123456", "senhadoclube",
        "senhadosistema", "senhadeacesso", "eutenhoumasenha", "naoseiasenha", "esqueciasenha", "esqueciminhasenha",
        "brasilbrasilbrasil", "flamengo123456789", "corinthians123456", "palmeiras1234567", "saopaulofutebolclube",
        "vascodagama12345", "gremio1234567890", "cruzeiro12345678", "botafogo12345678", "fluminense123456",
        "amorverdadeiro", "teamoparasempre", "euteamomuito", "familiafeliz", "minhafamilia123", "mamaeepapai",
        // Termos religiosos e do clube (contexto da aplicação).
        "deusefiel", "deuseamor", "deusmeama", "deuseomeupastor", "deusnocomando", "deusnocontrole", "jesusteama",
        "jesusmeama", "jesuscristo", "jesuscristoesenhor", "jesuscristosalva", "jesuseosenhor", "jesusvoltara",
        "jesusestavoltando", "oseuhorasejesus", "tudopossonaquelequemefortalece", "osenhoreomeupastor",
        "gracaepaz", "fidelidadeadeus", "adventistadosetimodia", "igrejaadventista",
        "igrejaadventistadosetimodia", "iasdvilajoani", "clubededesbravadores", "clubedesbravadores",
        "desbravadoresiasd", "desbravadoresadventistas", "almirantedesbravadores", "almirantedesbravador",
        "clubealmirante", "aventureirosedesbravadores", "desbravadorsempre", "sempredesbravador",
        "pelavidadosjovens", "salvardopecadoeguiarnoservico", "oamordecristomeconstrange", "amensagemdoadvento",
    };

    public static bool Contem(string senha)
    {
        var compacta = Compactar(senha);
        if (compacta.Length == 0) return false;
        var semSufixoNumerico = compacta.TrimEnd("0123456789".ToCharArray());
        return Valores.Contains(compacta) || (semSufixoNumerico.Length > 0 && Valores.Contains(semSufixoNumerico));
    }

    /// <summary>Minúsculas, sem acentos e só letras e dígitos (com surrogate inválido, que não normaliza, compara sem remover acentos).</summary>
    private static string Compactar(string senha)
    {
        var minuscula = senha.ToLowerInvariant();
        string decomposta;
        try { decomposta = minuscula.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposta = minuscula; }
        var resultado = new StringBuilder(decomposta.Length);
        foreach (var c in decomposta)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                resultado.Append(c);
        }
        return resultado.ToString();
    }
}
