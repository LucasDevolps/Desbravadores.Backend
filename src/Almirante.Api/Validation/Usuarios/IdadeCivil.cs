namespace Almirante.Api.Validation.Usuarios;

public static class IdadeCivil
{
    public const int Maioridade = 18;

    public static DateOnly DataDaMaioridade(DateOnly nascimento)
    {
        var ano = nascimento.Year + Maioridade;
        if (ano > DateOnly.MaxValue.Year)
        {
            return DateOnly.MaxValue;
        }

        return nascimento is { Month: 2, Day: 29 }
            ? new DateOnly(ano, 3, 1)
            : new DateOnly(ano, nascimento.Month, nascimento.Day);
    }

    public static bool EhMenorDeIdade(DateOnly nascimento, DateOnly hoje) => hoje < DataDaMaioridade(nascimento);
}
