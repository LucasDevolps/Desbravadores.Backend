namespace Almirante.Api.Validation.Usuarios;

// Maioridade civil (18 anos) contada no calendário, no nível do dia (issues #112 e #131): a pessoa é menor até a
// véspera do 18º aniversário. Quem nasceu em 29/02 completa 18 anos em 01/03, porque o prazo em anos vence "no imediato,
// se faltar exata correspondência" (Código Civil, art. 132, §3º) e o 18º aniversário de quem nasceu num ano bissexto
// nunca cai em ano bissexto (18 = 2 mod 4). DateOnly.AddYears levaria a 28/02.
public static class IdadeCivil
{
    public const int Maioridade = 18;

    public static DateOnly DataDaMaioridade(DateOnly nascimento)
    {
        var ano = nascimento.Year + Maioridade;
        if (ano > DateOnly.MaxValue.Year)
        {
            // Só com dado corrompido (nascimento perto do ano 9999): nunca maior de idade, sem estourar o DateOnly.
            return DateOnly.MaxValue;
        }

        return nascimento is { Month: 2, Day: 29 }
            ? new DateOnly(ano, 3, 1)
            : new DateOnly(ano, nascimento.Month, nascimento.Day);
    }

    public static bool EhMenorDeIdade(DateOnly nascimento, DateOnly hoje) => hoje < DataDaMaioridade(nascimento);
}
