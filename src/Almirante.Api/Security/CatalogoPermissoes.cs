using System.Collections.Frozen;

namespace Almirante.Api.Security;

/// <summary>
/// Chaves técnicas de permissão (issue #182). São contrato com o código: cada endpoint declara a capacidade que exige com
/// <see cref="RequerPermissaoAttribute"/>, e o banco decide quais cargos a recebem (CargoPermissoes). Inserir uma chave nova no banco não cria
/// comportamento; uma capacidade nova exige desenvolvimento, uma entrada em <see cref="CatalogoPermissoes.Todas"/> e uma migration.
/// </summary>
public static class Permissoes
{
    public const string UsuariosConsultar = "usuarios.consultar";
    public const string UsuariosCriar = "usuarios.criar";
    public const string UsuariosEditar = "usuarios.editar";
    public const string UsuariosExcluir = "usuarios.excluir";
    public const string UsuariosFotoAlterar = "usuarios.foto.alterar";
    public const string UsuariosSenhaAlterarTerceiros = "usuarios.senha.alterar-terceiros";
    public const string UsuariosSenhaRecuperacaoAtender = "usuarios.senha.recuperacao-atender";
    public const string CargosConsultar = "cargos.consultar";

    public const string LancamentosConsultar = "financeiro.lancamentos.consultar";
    public const string LancamentosCriar = "financeiro.lancamentos.criar";
    public const string LancamentosEditar = "financeiro.lancamentos.editar";
    public const string LancamentosExcluir = "financeiro.lancamentos.excluir";
    public const string PagamentosRegistrar = "financeiro.pagamentos.registrar";
    public const string TesourariaConsultar = "financeiro.tesouraria.consultar";

    public const string EventosConsultar = "eventos.consultar";
    public const string EventosCriar = "eventos.criar";
    public const string EventosEditar = "eventos.editar";
    public const string EventosExcluir = "eventos.excluir";

    public const string MembrosConsultar = "membros.consultar";

    public const string DashboardConsultar = "dashboard.consultar";
    public const string DashboardFinanceiroConsultar = "dashboard.financeiro.consultar";

    public const string FormacaoGestaoConsultar = "formacao.gestao.consultar";
    public const string FormacaoGerenciar = "formacao.gerenciar";
    public const string FormacaoRevisoesAdministrar = "formacao.revisoes.administrar";

    public const string AlmoxarifadoConsultar = "almoxarifado.consultar";
    public const string AlmoxarifadoGerenciar = "almoxarifado.gerenciar";
    public const string AlmoxarifadoResponsaveisDesignar = "almoxarifado.responsaveis.designar";

    public const string AutorizacaoConsultar = "autorizacao.consultar";
    public const string AutorizacaoCargosGerenciar = "autorizacao.cargos.gerenciar";
    public const string AutorizacaoConcessoesGerenciar = "autorizacao.concessoes.gerenciar";
    public const string AutorizacaoRegrasAlvoGerenciar = "autorizacao.regras-alvo.gerenciar";
}

/// <summary>Uma capacidade do catálogo técnico. <see cref="Id"/> é fixo: a migration e o seed usam o mesmo valor.</summary>
/// <param name="Escrita">Altera dados (a tela separa leitura e escrita).</param>
/// <param name="ReservadaSistema">Administração de autorização: só o perfil de sistema protegido a recebe nesta versão.</param>
/// <param name="ExigeRegraAlvo">Além da concessão, o par cargo solicitante → cargo alvo precisa estar em CargoRegrasAlvo.</param>
public sealed record DefinicaoPermissao(Guid Id, string Chave, string Modulo, string Descricao, bool Escrita, bool ReservadaSistema,
    bool ExigeRegraAlvo);

public static class CatalogoPermissoes
{
    public const int ChaveMaximo = 150;
    public const int ModuloMaximo = 60;
    public const int DescricaoMaximo = 500;

    private static DefinicaoPermissao P(int n, string chave, string modulo, string descricao, bool escrita = false, bool reservada = false,
        bool alvo = false) =>
        new(new Guid($"18200000-0000-4000-8000-{n:D12}"), chave, modulo, descricao, escrita, reservada, alvo);

    public static readonly IReadOnlyList<DefinicaoPermissao> Todas =
    [
        P(1, Permissoes.UsuariosConsultar, "Usuários", "Consultar o cadastro completo de usuários e a foto de outros usuários."),
        P(2, Permissoes.UsuariosCriar, "Usuários", "Cadastrar usuários nos cargos permitidos pelas regras por alvo.", escrita: true, alvo: true),
        P(3, Permissoes.UsuariosEditar, "Usuários", "Alterar o cadastro e o cargo de usuários nos cargos permitidos pelas regras por alvo.", escrita: true, alvo: true),
        P(4, Permissoes.UsuariosExcluir, "Usuários", "Excluir (logicamente) usuários nos cargos permitidos pelas regras por alvo.", escrita: true, alvo: true),
        P(5, Permissoes.UsuariosFotoAlterar, "Usuários", "Enviar ou remover a foto de usuários nos cargos permitidos pelas regras por alvo.", escrita: true, alvo: true),
        P(6, Permissoes.UsuariosSenhaAlterarTerceiros, "Usuários", "Alterar a senha de usuários nos cargos permitidos pelas regras por alvo.", escrita: true, alvo: true),
        P(7, Permissoes.UsuariosSenhaRecuperacaoAtender, "Usuários", "Listar e atender pedidos de recuperação de senha nos cargos permitidos pelas regras por alvo.", escrita: true, alvo: true),
        P(8, Permissoes.CargosConsultar, "Usuários", "Consultar os cargos para o cadastro de usuários."),

        P(9, Permissoes.LancamentosConsultar, "Financeiro", "Consultar lançamentos."),
        P(10, Permissoes.LancamentosCriar, "Financeiro", "Registrar lançamentos.", escrita: true),
        P(11, Permissoes.LancamentosEditar, "Financeiro", "Corrigir lançamentos.", escrita: true),
        P(12, Permissoes.LancamentosExcluir, "Financeiro", "Excluir lançamentos.", escrita: true),
        P(13, Permissoes.PagamentosRegistrar, "Financeiro", "Registrar e corrigir pagamentos de lançamentos.", escrita: true),
        P(14, Permissoes.TesourariaConsultar, "Financeiro", "Consultar o resumo da tesouraria."),

        P(15, Permissoes.EventosConsultar, "Eventos", "Consultar eventos."),
        P(16, Permissoes.EventosCriar, "Eventos", "Cadastrar eventos (e as cobranças que eles geram).", escrita: true),
        P(17, Permissoes.EventosEditar, "Eventos", "Alterar eventos (e sincronizar as cobranças).", escrita: true),
        P(18, Permissoes.EventosExcluir, "Eventos", "Excluir eventos.", escrita: true),

        P(19, Permissoes.MembrosConsultar, "Membros", "Consultar a lista mínima de membros (id, nome e cargo) para seleção."),

        P(20, Permissoes.DashboardConsultar, "Dashboard", "Consultar o painel."),
        P(21, Permissoes.DashboardFinanceiroConsultar, "Dashboard", "Ver o bloco financeiro do painel."),

        P(22, Permissoes.FormacaoGestaoConsultar, "Formação", "Consultar a formação de qualquer membro e os relatórios de formação."),
        P(23, Permissoes.FormacaoGerenciar, "Formação", "Registrar a formação dos membros e manter o catálogo de especialidades.", escrita: true),
        P(24, Permissoes.FormacaoRevisoesAdministrar, "Formação", "Criar, publicar e descartar revisões de requisitos e ver rascunhos.", escrita: true),

        P(25, Permissoes.AlmoxarifadoConsultar, "Almoxarifado", "Consultar bens, empréstimos, inventários, desejos e relatórios do almoxarifado."),
        P(26, Permissoes.AlmoxarifadoGerenciar, "Almoxarifado", "Cadastrar e movimentar bens, inventários e a lista de desejos.", escrita: true),
        P(27, Permissoes.AlmoxarifadoResponsaveisDesignar, "Almoxarifado", "Designar e revogar o responsável pelo almoxarifado.", escrita: true),

        P(28, Permissoes.AutorizacaoConsultar, "Autorização", "Consultar o catálogo de permissões, as concessões e as regras por alvo dos cargos.", reservada: true),
        P(29, Permissoes.AutorizacaoCargosGerenciar, "Autorização", "Criar, alterar, desativar e reativar cargos.", escrita: true, reservada: true),
        P(30, Permissoes.AutorizacaoConcessoesGerenciar, "Autorização", "Conceder e revogar permissões de cargos.", escrita: true, reservada: true),
        P(31, Permissoes.AutorizacaoRegrasAlvoGerenciar, "Autorização", "Alterar as regras por alvo dos cargos.", escrita: true, reservada: true),
    ];

    public static readonly FrozenDictionary<string, DefinicaoPermissao> PorChave =
        Todas.ToFrozenDictionary(p => p.Chave, StringComparer.Ordinal);

    /// <summary>
    /// Capacidades que a designação vigente de responsável pelo almoxarifado concede, além do cargo. Não inclui designar responsáveis
    /// nem nada de autorização.
    /// </summary>
    public static readonly IReadOnlyList<string> PorDesignacaoAlmoxarifado = [Permissoes.AlmoxarifadoConsultar, Permissoes.AlmoxarifadoGerenciar];
}
