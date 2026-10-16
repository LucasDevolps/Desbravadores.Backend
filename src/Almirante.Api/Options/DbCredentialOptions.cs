namespace Almirante.Api.Options;

// Usuário SQL dedicado da aplicação (menor privilégio) com senha rotacionada pela própria API.
// Desligado por padrão: sem AppUser, a API usa ConnectionStrings:almirante exatamente como antes
// (ex.: IIS com Windows Auth, AppHost/Aspire, testes). Quando ligado, ConnectionStrings:almirante
// NÃO pode conter credenciais (elas são injetadas a cada conexão) e ConnectionStrings:AlmiranteAdmin
// (conta com permissão de DDL/ALTER LOGIN) passa a ser usada só para migrations e para provisionar/
// rotacionar a senha do AppUser.
public sealed class DbCredentialOptions
{
    public const string SectionName = "DbCredentials";

    public string? AppUser { get; init; }

    // Intervalo entre rotações da senha do AppUser (além da rotação feita em todo startup).
    public int RotationHours { get; init; } = 24;

    public bool Enabled => !string.IsNullOrWhiteSpace(AppUser);
}
