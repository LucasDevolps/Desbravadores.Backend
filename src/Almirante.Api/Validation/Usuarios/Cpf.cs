namespace Almirante.Api.Validation.Usuarios;

public static class Cpf
{
    public const int Digitos = 11;

    public static bool EhValido(string? cpf)
    {
        if (cpf is not { Length: Digitos } || !cpf.All(char.IsAsciiDigit) || cpf.All(c => c == cpf[0]))
        {
            return false;
        }

        return DigitoVerificador(cpf, 9) == cpf[9] - '0' && DigitoVerificador(cpf, 10) == cpf[10] - '0';
    }

    private static int DigitoVerificador(string cpf, int quantidade)
    {
        var soma = 0;
        for (var i = 0; i < quantidade; i++)
        {
            soma += (cpf[i] - '0') * (quantidade + 1 - i);
        }

        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }
}
