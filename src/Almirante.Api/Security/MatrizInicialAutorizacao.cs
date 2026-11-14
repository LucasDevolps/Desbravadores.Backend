namespace Almirante.Api.Security;

/// <summary>
/// Concessões e regras por alvo do bootstrap (issue #182), equivalentes ao comportamento estático anterior (Roles/Policies e PermissoesSenha,
/// issues #127, #159, #125 e #126). Usada só para semear os 17 cargos conhecidos: no banco novo pelo DbSeeder (ao criar os cargos) e no banco
/// existente pela migration AddRbacDinamico. Cargos customizados não recebem nada por aproximação. Depois do bootstrap, a fonte de verdade é o
/// banco: reiniciar a API não restaura concessões revogadas.
/// </summary>
public static class MatrizInicialAutorizacao
{
    /// <summary>Os 17 cargos semeados pelo DbSeeder.</summary>
    public static readonly string[] CargosConhecidos =
        [Roles.Admin, Roles.Diretor, Roles.DiretorAssociado, Roles.Secretario, Roles.Tesoureiro, "CAP", "CONS", "CONSA", "INST", "CPT", "SECU",
         "TESU", Roles.Almoxarife, "PAD", "COREC", "CAPU", Roles.Desbravador];

    private static readonly string[] Diretoria = [Roles.Admin, Roles.Diretor, Roles.DiretorAssociado];

    private static readonly string[] Cadastro =
        [Permissoes.UsuariosConsultar, Permissoes.UsuariosCriar, Permissoes.UsuariosEditar, Permissoes.UsuariosExcluir,
         Permissoes.UsuariosFotoAlterar, Permissoes.CargosConsultar];

    private static readonly string[] Senhas = [Permissoes.UsuariosSenhaAlterarTerceiros, Permissoes.UsuariosSenhaRecuperacaoAtender];

    private static readonly string[] Financeiro =
        [Permissoes.LancamentosConsultar, Permissoes.LancamentosCriar, Permissoes.LancamentosEditar, Permissoes.LancamentosExcluir,
         Permissoes.PagamentosRegistrar, Permissoes.TesourariaConsultar, Permissoes.DashboardFinanceiroConsultar];

    private static readonly string[] Gestao =
        [Permissoes.EventosConsultar, Permissoes.EventosCriar, Permissoes.EventosEditar, Permissoes.EventosExcluir, Permissoes.MembrosConsultar,
         Permissoes.DashboardConsultar];

    private static readonly string[] Formacao =
        [Permissoes.FormacaoGestaoConsultar, Permissoes.FormacaoGerenciar, Permissoes.FormacaoRevisoesAdministrar];

    private static readonly string[] AlmoxarifadoGestao =
        [Permissoes.AlmoxarifadoConsultar, Permissoes.AlmoxarifadoGerenciar];

    private static readonly string[] Autorizacao =
        [Permissoes.AutorizacaoConsultar, Permissoes.AutorizacaoCargosGerenciar, Permissoes.AutorizacaoConcessoesGerenciar,
         Permissoes.AutorizacaoRegrasAlvoGerenciar];

    /// <summary>Permissões iniciais por role. Cargos conhecidos ausentes daqui (só DS) não recebem nenhuma.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Concessoes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Roles.Admin] = [.. Cadastro, .. Senhas, .. Financeiro, .. Gestao, .. Formacao, .. AlmoxarifadoGestao,
            Permissoes.AlmoxarifadoResponsaveisDesignar, .. Autorizacao],
        [Roles.Diretor] = [.. Cadastro, .. Senhas, .. Financeiro, .. Gestao, .. Formacao, .. AlmoxarifadoGestao,
            Permissoes.AlmoxarifadoResponsaveisDesignar],
        [Roles.DiretorAssociado] = [.. Cadastro, .. Financeiro, .. Gestao, .. Formacao, .. AlmoxarifadoGestao,
            Permissoes.AlmoxarifadoResponsaveisDesignar],
        [Roles.Secretario] = [.. Cadastro, .. Senhas, .. Gestao, .. Formacao, .. AlmoxarifadoGestao, Permissoes.AlmoxarifadoResponsaveisDesignar],
        [Roles.Tesoureiro] = [.. Financeiro, .. Gestao, Permissoes.AlmoxarifadoConsultar],
        [Roles.Almoxarife] = [.. AlmoxarifadoGestao],
        ["CAP"] = [Permissoes.AlmoxarifadoConsultar],
        ["CONS"] = [Permissoes.AlmoxarifadoConsultar],
        ["CONSA"] = [Permissoes.AlmoxarifadoConsultar],
        ["INST"] = [Permissoes.AlmoxarifadoConsultar],
        ["CPT"] = [Permissoes.AlmoxarifadoConsultar],
        ["SECU"] = [Permissoes.AlmoxarifadoConsultar],
        ["TESU"] = [Permissoes.AlmoxarifadoConsultar],
        ["PAD"] = [Permissoes.AlmoxarifadoConsultar],
        ["COREC"] = [Permissoes.AlmoxarifadoConsultar],
        ["CAPU"] = [Permissoes.AlmoxarifadoConsultar],
    };

    /// <summary>Operações de cadastro sobre outro usuário que seguem a antiga hierarquia de <c>Roles.PodeGerenciar</c>.</summary>
    private static readonly string[] OperacoesDeCadastro =
        [Permissoes.UsuariosCriar, Permissoes.UsuariosEditar, Permissoes.UsuariosExcluir, Permissoes.UsuariosFotoAlterar];

    private static readonly string[] CargosDeGestao = [Roles.Diretor, Roles.DiretorAssociado, Roles.Secretario, Roles.Tesoureiro];

    /// <summary>A antiga hierarquia: ADM só por ADM; DIR, DIRA, SEC e TES só pela diretoria; os demais cargos conhecidos por quem cadastra.</summary>
    private static bool GerenciavaAntes(string solicitante, string alvo) =>
        alvo == Roles.Admin ? solicitante == Roles.Admin
        : !CargosDeGestao.Contains(alvo) || Diretoria.Contains(solicitante);

    /// <summary>Matriz de senha da issue #159: ADM e DIR → SEC, TES, DS; SEC → DS; nenhum outro par.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> AlvosDeSenha = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Roles.Admin] = [Roles.Secretario, Roles.Tesoureiro, Roles.Desbravador],
        [Roles.Diretor] = [Roles.Secretario, Roles.Tesoureiro, Roles.Desbravador],
        [Roles.Secretario] = [Roles.Desbravador],
    };

    /// <summary>Regras por alvo iniciais (role solicitante, chave da permissão, role alvo), só entre cargos conhecidos.</summary>
    public static IReadOnlyList<(string Solicitante, string Permissao, string Alvo)> RegrasAlvo { get; } = MontarRegras();

    private static List<(string, string, string)> MontarRegras()
    {
        var regras = new List<(string, string, string)>();
        foreach (var (solicitante, chaves) in Concessoes)
        {
            foreach (var operacao in OperacoesDeCadastro.Where(chaves.Contains))
            {
                regras.AddRange(CargosConhecidos.Where(alvo => GerenciavaAntes(solicitante, alvo)).Select(alvo => (solicitante, operacao, alvo)));
            }

            if (AlvosDeSenha.TryGetValue(solicitante, out var alvosSenha))
            {
                foreach (var operacao in Senhas.Where(chaves.Contains))
                {
                    regras.AddRange(alvosSenha.Select(alvo => (solicitante, operacao, alvo)));
                }
            }
        }

        return regras;
    }

    /// <summary>Roles com <see cref="Entities.Cargo.SistemaProtegido"/> no bootstrap: só o Administrador.</summary>
    public static readonly string[] Protegidos = [Roles.Admin];
}
