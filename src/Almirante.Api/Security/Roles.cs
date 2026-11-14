using Microsoft.AspNetCore.Authorization;

namespace Almirante.Api.Security;

/// <summary>
/// Roles dos cargos semeados. Desde a issue #182 nenhuma delas decide acesso: as permissões e as regras por alvo vêm do banco
/// (CargoPermissoes e CargoRegrasAlvo). Sobram como identificadores do bootstrap (<see cref="MatrizInicialAutorizacao"/>, DbSeeder) e, no
/// caso de <see cref="Desbravador"/>, como classificação de negócio da contagem do dashboard.
/// </summary>
public static class Roles
{
    public const string Admin = "ADM";
    public const string Diretor = "DIR";
    public const string DiretorAssociado = "DIRA";
    public const string Secretario = "SEC";
    public const string Tesoureiro = "TES";

    /// <summary>Almoxarife (cargo semeado; a migration do almoxarifado o cria se não existir).</summary>
    public const string Almoxarife = "ALM";

    /// <summary>Cargo dos desbravadores (membros do clube): classificação usada na contagem do dashboard, não permissão.</summary>
    public const string Desbravador = "DS";
}

public static class Policies
{
    /// <summary>
    /// Dados, foto, eventos, formação e permissões do próprio usuário, consulta de CEP e dos catálogos de classes e especialidades: qualquer
    /// cargo ativo (a identidade vem só do token). As demais rotas exigem uma permissão (<see cref="RequerPermissaoAttribute"/>).
    /// </summary>
    public const string Autoatendimento = "Autoatendimento";

    public static void Registrar(AuthorizationOptions options)
    {
        options.AddPolicy(Autoatendimento, policy => policy.RequireAuthenticatedUser());
    }
}
