namespace Almirante.Api.Security;

public static class Roles
{
    public const string Admin = "ADM";
    public const string Diretor = "DIR";
    public const string DiretorAssociado = "DIRA";
    public const string Secretario = "SEC";
    public const string Tesoureiro = "TES";

    /// <summary>Cargo dos desbravadores (membros do clube); usado na contagem do dashboard, sem acesso administrativo.</summary>
    public const string Desbravador = "DS";

    public static readonly string[] Diretoria = [Admin, Diretor, DiretorAssociado, Secretario, Tesoureiro];
}

public static class Policies
{
    public const string GestaoFinanceira = "GestaoFinanceira";
    public const string GestaoCadastros = "GestaoCadastros";
}
