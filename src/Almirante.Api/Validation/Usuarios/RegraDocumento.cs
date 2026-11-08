namespace Almirante.Api.Validation.Usuarios;

public static class RegraDocumento
{
    /// <summary>
    /// Documento completo (CPF com RG, ou CPF com CIN) não muda mais: cpf, rg e ehCin efetivos precisam ser os gravados. Cadastro
    /// antigo com documento incompleto continua podendo ser completado.
    /// </summary>
    public static Dictionary<string, string[]> ValidarAlteracao(string? cpfGravado, string? rgGravado, bool ehCinGravado,
        string? cpf, string? rg, bool ehCin)
    {
        var erros = new Dictionary<string, string[]>();
        if (cpfGravado is null || (rgGravado is null && !ehCinGravado))
        {
            return erros;
        }

        if (cpf != cpfGravado)
        {
            erros["Cpf"] = ["cpf não pode ser alterado: o documento do usuário (CPF com RG ou com CIN) já está cadastrado."];
        }

        if (rg != rgGravado)
        {
            erros["Rg"] = ["rg não pode ser alterado: o documento do usuário (CPF com RG ou com CIN) já está cadastrado."];
        }

        if (ehCin != ehCinGravado)
        {
            erros["EhCin"] = ["ehCin não pode ser alterado: o documento do usuário (CPF com RG ou com CIN) já está cadastrado."];
        }

        return erros;
    }
}
