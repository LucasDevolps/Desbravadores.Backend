namespace Almirante.Api.Options;

// Espelha a seção padrão "ConnectionStrings" só para permitir validação via IValidateOptions
// (SqlServerConnectionSecurityValidator); a resolução em tempo de execução continua sendo feita
// pelo Aspire (AddSqlServerDbContext, em Program.cs) diretamente sobre a configuração.
public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";

    public string? Almirante { get; init; }

    // Conexão administrativa (migrations + rotação de senha do usuário da aplicação); ver DbCredentialOptions.
    public string? AlmiranteAdmin { get; init; }
}
