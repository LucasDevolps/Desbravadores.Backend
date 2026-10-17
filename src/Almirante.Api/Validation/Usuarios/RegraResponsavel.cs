namespace Almirante.Api.Validation.Usuarios;

public static class RegraResponsavel
{
    public static Dictionary<string, string[]> Validar(DateOnly? nascimento, string? nome, string? email,
        string? cpfResponsavel, string? cpfUsuario, DateOnly hoje)
    {
        var erros = new Dictionary<string, string[]>();
        if (nascimento is { } data && IdadeCivil.EhMenorDeIdade(data, hoje))
        {
            if (nome is null)
            {
                erros["ResponsavelNome"] = ["responsavelNome é obrigatório para menor de 18 anos."];
            }

            if (email is null)
            {
                erros["ResponsavelEmail"] = ["responsavelEmail é obrigatório para menor de 18 anos."];
            }
        }

        if (cpfResponsavel is not null && cpfResponsavel == cpfUsuario)
        {
            erros["ResponsavelCpf"] = ["responsavelCpf não pode ser igual ao cpf do próprio usuário."];
        }

        return erros;
    }
}
