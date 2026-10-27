using System.Collections.Frozen;

namespace Almirante.Api.Security;

/// <summary>
/// Matriz explícita de quem altera a senha de quem (issue #159): só os pares listados, sem hierarquia inferida. ADM e DIR alteram SEC, TES e
/// DS; SEC altera DS; DIRA, TES, DS e os demais cargos não alteram terceiros, e ADM, DIR, DIRA e os demais cargos nunca são alvo. A troca da
/// própria senha (com a senha atual) vale para qualquer usuário autenticado e não passa por aqui. A mesma matriz decide quem lista pedidos
/// de recuperação, quem emite uma autorização e se a autorização ainda vale quando o titular a consome. Mais restrita que
/// <see cref="Roles.PodeGerenciar"/> e <see cref="Policies.GestaoCadastros"/>: editar o cadastro não dá direito de trocar a senha.
/// </summary>
public static class PermissoesSenha
{
    private static readonly FrozenDictionary<string, string[]> AlvosPorSolicitante = new Dictionary<string, string[]>
    {
        [Roles.Admin] = [Roles.Secretario, Roles.Tesoureiro, Roles.Desbravador],
        [Roles.Diretor] = [Roles.Secretario, Roles.Tesoureiro, Roles.Desbravador],
        [Roles.Secretario] = [Roles.Desbravador],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Cargos que alteram a senha de algum terceiro: base da policy <see cref="Policies.AlteracaoSenhaTerceiros"/>.</summary>
    public static readonly string[] Solicitantes = [Roles.Admin, Roles.Diretor, Roles.Secretario];

    public static IReadOnlyList<string> AlvosPermitidos(string solicitante) =>
        AlvosPorSolicitante.TryGetValue(solicitante, out var alvos) ? alvos : [];

    public static bool PodeAlterarSenhaDeTerceiro(string solicitante, string alvo) =>
        AlvosPermitidos(solicitante).Contains(alvo, StringComparer.Ordinal);

    /// <summary>Se algum cargo pode atender este alvo (SEC, TES, DS). Pedidos públicos de outros cargos não são guardados: ninguém os atenderia.</summary>
    public static bool PodeSerAlvo(string alvo) => Solicitantes.Any(solicitante => PodeAlterarSenhaDeTerceiro(solicitante, alvo));
}
