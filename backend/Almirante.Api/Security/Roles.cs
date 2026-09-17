namespace Almirante.Api.Security;

public static class Roles
{
    public const string Admin = "ADM";
    public const string Diretor = "DIR";
    public const string DiretorAssociado = "DIRA";
    public const string Secretario = "SEC";
    public const string Tesoureiro = "TES";

    // Papéis da diretoria do clube. Mesmo conjunto que já protegia Lançamentos (PR #41); as listagens
    // administrativas usam o mesmo grupo porque o registro de lançamentos depende de selecionar
    // membros (Usuarios) e esses são os papéis que fazem cadastro/finanças.
    public static readonly string[] Diretoria = [Admin, Diretor, DiretorAssociado, Secretario, Tesoureiro];
}

// Matriz de acesso (fonte única; ver também docs/authentication-security.md):
//   GestaoFinanceira -> /api/Lancamentos (GET, POST Registrar, PUT, DELETE)
//   GestaoCadastros  -> GET /api/Usuarios, GET /api/Cargos
//   Qualquer autenticado -> GET /api/Auth/Me (somente o próprio perfil)
public static class Policies
{
    public const string GestaoFinanceira = "GestaoFinanceira";
    public const string GestaoCadastros = "GestaoCadastros";
}
