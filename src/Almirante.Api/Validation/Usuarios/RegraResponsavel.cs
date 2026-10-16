namespace Almirante.Api.Validation.Usuarios;

// Dados do responsável (issues #112 e #131): nome e e-mail são exigidos só quando o usuário é menor de 18 anos na data
// civil de hoje; para maior de idade, ou sem data de nascimento, tudo é opcional. Recebe os valores EFETIVOS e já
// normalizados: no POST, os do corpo; no PUT, os do corpo ou, quando omitidos, os do banco lidos sob o lock da
// alteração ("informada ou recuperada do banco", #131). O formato de cada campo é validado à parte (validators).
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
