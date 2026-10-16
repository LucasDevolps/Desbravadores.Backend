namespace Almirante.Api.Validation.Usuarios;

// CPF: 11 dígitos numéricos com os dois dígitos verificadores do módulo 11 (issue #112; o identificador alfanumérico
// de 2026 é do CNPJ, não do CPF). Recebe o valor JÁ normalizado por UsuariosService.NormalizarCpf. Sequências de um
// único dígito (000.000.000-00 a 999.999.999-99) passam no módulo 11, mas não são CPFs e são recusadas.
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

    // Soma dos "quantidade" primeiros dígitos com pesos decrescentes até 2; resto < 2 vira 0, senão 11 - resto.
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
