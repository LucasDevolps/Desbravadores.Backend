using Microsoft.AspNetCore.Authorization;

namespace Almirante.Api.Security;

public static class Roles
{
    public const string Admin = "ADM";
    public const string Diretor = "DIR";
    public const string DiretorAssociado = "DIRA";
    public const string Secretario = "SEC";
    public const string Tesoureiro = "TES";

    /// <summary>Almoxarife (cargo semeado; a migration do almoxarifado o cria se não existir). Gerencia o almoxarifado do clube (issue #126).</summary>
    public const string Almoxarife = "ALM";

    /// <summary>Cargo dos desbravadores (membros do clube); usado na contagem do dashboard, sem acesso administrativo.</summary>
    public const string Desbravador = "DS";

    /// <summary>Acesso completo aos módulos de negócio. ADM também é o administrador técnico (única exceção: usuários ADM).</summary>
    public static readonly string[] Diretoria = [Admin, Diretor, DiretorAssociado];

    /// <summary>Usuários, fotos de terceiros, cargos e CEP no cadastro.</summary>
    public static readonly string[] Cadastros = [.. Diretoria, Secretario];

    /// <summary>Lançamentos, pagamentos e indicadores financeiros.</summary>
    public static readonly string[] Financeiro = [.. Diretoria, Tesoureiro];

    /// <summary>Eventos (cadastro e cobranças geradas por ele), consulta mínima de membros e painel.</summary>
    public static readonly string[] Gestao = [.. Diretoria, Secretario, Tesoureiro];

    /// <summary>
    /// Escrita e visão administrativa do acompanhamento de classes, especialidades e requisitos (issue #125). Tesoureiro, instrutor e
    /// demais cargos só consultam catálogos e a própria formação.
    /// </summary>
    public static readonly string[] Formacao = [.. Diretoria, Secretario];

    /// <summary>
    /// Gestão do almoxarifado (patrimônio, movimentações, inventários e lista de desejos, issue #126). Além destes cargos, o membro
    /// designado como responsável pelo almoxarifado (almoxarifado_responsaveis) também gerencia, mantendo o próprio cargo.
    /// </summary>
    public static readonly string[] Almoxarifado = [.. Diretoria, Secretario, Almoxarife];

    /// <summary>Cargos sem consulta ao almoxarifado: o Desbravador (DS). Os demais cargos ativos consultam.</summary>
    public static readonly string[] SemConsultaAlmoxarifado = [Desbravador];

    /// <summary>Cargos que dão acesso a algum módulo de gestão: só a diretoria atribui ou gerencia usuários com eles.</summary>
    public static readonly string[] CargosDeGestao = [Diretor, DiretorAssociado, Secretario, Tesoureiro];

    /// <summary>
    /// Se quem tem o cargo <paramref name="solicitante"/> pode criar, alterar, excluir ou trocar a foto de um usuário com o cargo
    /// <paramref name="alvo"/> (e atribuir esse cargo). ADM só por ADM; cargos de gestão só pela diretoria (impede que o secretário
    /// conceda a si ou a outra conta acesso financeiro ou de diretoria); os demais por quem tem a policy de cadastros.
    /// </summary>
    public static bool PodeGerenciar(string solicitante, string alvo) =>
        alvo == Admin ? solicitante == Admin
        : !CargosDeGestao.Contains(alvo) || Diretoria.Contains(solicitante);
}

public static class Policies
{
    /// <summary>Usuários (CRUD e fotos de terceiros) e cargos: ADM, DIR, DIRA, SEC.</summary>
    public const string GestaoCadastros = "GestaoCadastros";

    /// <summary>Lançamentos, pagamentos e indicadores financeiros do painel: ADM, DIR, DIRA, TES.</summary>
    public const string GestaoFinanceira = "GestaoFinanceira";

    /// <summary>Cadastro, edição e exclusão de eventos (com as cobranças que eles geram): ADM, DIR, DIRA, SEC, TES.</summary>
    public const string GestaoEventos = "GestaoEventos";

    /// <summary>Lista mínima de membros (id, nome, cargo) para selecionar participantes e cobrados: ADM, DIR, DIRA, SEC, TES.</summary>
    public const string ConsultaMembros = "ConsultaMembros";

    /// <summary>Painel: ADM, DIR, DIRA, SEC, TES; o bloco financeiro só para quem também tem <see cref="GestaoFinanceira"/>.</summary>
    public const string Painel = "Painel";

    /// <summary>Catálogos, requisitos e acompanhamento de qualquer membro (escrita e consulta administrativa): ADM, DIR, DIRA, SEC.</summary>
    public const string GestaoFormacao = "GestaoFormacao";

    /// <summary>
    /// Gestão do almoxarifado: cadastro e movimentação de bens, inventários e lista de desejos. ADM, DIR, DIRA, SEC, ALM e o responsável
    /// designado (conferido no banco a cada requisição).
    /// </summary>
    public const string GestaoAlmoxarifado = "GestaoAlmoxarifado";

    /// <summary>Consulta do almoxarifado (listas, históricos e relatórios): qualquer cargo ativo exceto DS, e o responsável designado.</summary>
    public const string ConsultaAlmoxarifado = "ConsultaAlmoxarifado";

    /// <summary>Designar e revogar o responsável pelo almoxarifado: ADM, DIR, DIRA, SEC (quem também atribui o cargo ALM).</summary>
    public const string DesignacaoAlmoxarifado = "DesignacaoAlmoxarifado";

    /// <summary>
    /// Dados, foto, eventos e formação do próprio usuário, consulta de CEP e dos catálogos de classes e especialidades: qualquer cargo
    /// ativo (a identidade vem só do token).
    /// </summary>
    public const string Autoatendimento = "Autoatendimento";

    /// <summary>
    /// Filtro inicial das rotas de senha de terceiros e de recuperação assistida: ADM, DIR, SEC (<see cref="PermissoesSenha.Solicitantes"/>).
    /// Não basta: o par solicitante/alvo é decidido com os cargos relidos do banco pela matriz de <see cref="PermissoesSenha"/>.
    /// </summary>
    public const string AlteracaoSenhaTerceiros = "AlteracaoSenhaTerceiros";

    public static void Registrar(AuthorizationOptions options)
    {
        options.AddPolicy(GestaoCadastros, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Cadastros));
        options.AddPolicy(GestaoFinanceira, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Financeiro));
        options.AddPolicy(GestaoEventos, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Gestao));
        options.AddPolicy(ConsultaMembros, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Gestao));
        options.AddPolicy(Painel, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Gestao));
        options.AddPolicy(GestaoFormacao, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Formacao));
        options.AddPolicy(Autoatendimento, policy => policy.RequireAuthenticatedUser());
        options.AddPolicy(AlteracaoSenhaTerceiros, policy => policy.RequireAuthenticatedUser().RequireRole(PermissoesSenha.Solicitantes));
        options.AddPolicy(GestaoAlmoxarifado, policy => policy.RequireAuthenticatedUser().AddRequirements(AcessoAlmoxarifadoRequirement.Gestao));
        options.AddPolicy(ConsultaAlmoxarifado, policy => policy.RequireAuthenticatedUser().AddRequirements(AcessoAlmoxarifadoRequirement.Consulta));
        options.AddPolicy(DesignacaoAlmoxarifado, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Cadastros));
    }
}
