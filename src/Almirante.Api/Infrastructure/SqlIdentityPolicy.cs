using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

// Política de identidades SQL do processo da API: "sa" (e o segredo dele) NUNCA entram aqui.
//
// A recusa é técnica, não documental — não existe modo "Warn", "Off" nem fallback:
//   - o processo não pode receber SQL_SA_PASSWORD/MSSQL_SA_PASSWORD (variável de ambiente ou chave de
//     configuração): se receber, o startup falha. Só o serviço "sqlserver" (que inicializa a instância) e o
//     "sql-bootstrap" (que provisiona as identidades da aplicação) do Compose conhecem esse segredo;
//   - nenhuma connection string da API (runtime e administrativa) pode usar o login "sa";
//   - com DbCredentials:AppUser habilitado, a identidade administrativa é obrigatória, dedicada (diferente
//     da de runtime e de "sa") e precisa de credencial.
// A identidade EFETIVA (sysadmin, CONTROL SERVER, SID 0x01 de um "sa" renomeado...) é conferida no SQL
// Server por DbPrivilegeAuditor; este arquivo cobre só o que dá para recusar olhando a configuração.
// Só devolve nomes de opções, nunca valores/connection strings, para poder ir a log e exceção de startup.
public static class SqlIdentityPolicy
{
    public const string AdminPrivilegeCheckSettingName = "Security:AdminPrivilegeCheck";

    private static readonly string[] SqlServerSaSecretNames = ["SQL_SA_PASSWORD", "MSSQL_SA_PASSWORD"];

    public static bool IsSaName(string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return false;
        var name = user.Trim();
        if (name.Length >= 2 && name[0] == '[' && name[^1] == ']') name = name[1..^1].Trim();
        return string.Equals(name, "sa", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> Validate(IConfiguration configuration)
    {
        var appUser = configuration.GetSection(DbCredentialOptions.SectionName)[nameof(DbCredentialOptions.AppUser)];
        return Validate(
            configuration.GetConnectionString("almirante"),
            configuration.GetConnectionString(DbCredentialManager.AdminConnectionName),
            appUser,
            configuration);
    }

    public static IEnumerable<string> Validate(string? runtimeConnection, string? adminConnection, string? appUser, IConfiguration? configuration = null)
    {
        var failures = new List<string>();

        if (configuration is not null)
        {
            foreach (var name in SqlServerSaSecretNames)
                if (configuration[name] is not null)
                    failures.Add($"{name} não pode existir no ambiente/configuração da API: o segredo do login 'sa' pertence só ao serviço " +
                        "do SQL Server e ao sql-bootstrap (a API usa as identidades ConnectionStrings:AlmiranteAdmin e DbCredentials:AppUser).");

            // O modo Warn/Off já existiu: uma configuração antiga que ainda o defina não pode passar a valer em silêncio.
            var check = configuration[AdminPrivilegeCheckSettingName];
            if (!string.IsNullOrWhiteSpace(check) && !string.Equals(check.Trim(), "Enforce", StringComparison.OrdinalIgnoreCase))
                failures.Add($"{AdminPrivilegeCheckSettingName} foi removida: a auditoria da identidade administrativa é sempre obrigatória. " +
                    "Retire essa configuração.");
        }

        SqlConnectionStringBuilder? runtime = TryParse(runtimeConnection);
        SqlConnectionStringBuilder? admin = TryParse(adminConnection);

        if (runtime is not null && IsSaName(runtime.UserID))
            failures.Add($"{ConnectionStringsOptions.SectionName}:almirante: o login 'sa' não pode ser usado pela API.");
        if (admin is not null && IsSaName(admin.UserID))
            failures.Add($"{ConnectionStringsOptions.SectionName}:{DbCredentialManager.AdminConnectionName}: o login 'sa' não pode ser usado pela API " +
                "(crie a identidade administrativa dedicada com docs/sql/criar-usuario-admin-app.sql).");

        if (string.IsNullOrWhiteSpace(appUser)) return failures;

        // Daqui em diante: DbCredentials:AppUser habilitado (runtime com credencial rotacionada + admin dedicado).
        if (IsSaName(appUser))
            failures.Add($"{DbCredentialOptions.SectionName}:AppUser não pode ser 'sa'.");

        if (runtime is not null)
        {
            if (!string.IsNullOrEmpty(runtime.UserID) || !string.IsNullOrEmpty(runtime.Password) || runtime.IntegratedSecurity)
                failures.Add($"{ConnectionStringsOptions.SectionName}:almirante não pode conter User Id/Password/Integrated Security quando " +
                    "DbCredentials:AppUser está definido (a credencial do usuário da aplicação é injetada em tempo de execução).");
            if (string.IsNullOrWhiteSpace(runtime.InitialCatalog))
                failures.Add($"{ConnectionStringsOptions.SectionName}:almirante precisa de Database=<banco> (a identidade de runtime é um usuário contido do banco).");
        }

        if (admin is null)
        {
            failures.Add($"{ConnectionStringsOptions.SectionName}:{DbCredentialManager.AdminConnectionName} é obrigatória quando " +
                "DbCredentials:AppUser está definido: a API não tem fallback para 'sa' (defina SQL_ADMIN_USER e SQL_ADMIN_PASSWORD com a " +
                "identidade criada por docs/sql/criar-usuario-admin-app.sql).");
            return failures;
        }

        var hasSqlCredential = !string.IsNullOrWhiteSpace(admin.UserID) && !string.IsNullOrEmpty(admin.Password);
        if (!hasSqlCredential && !admin.IntegratedSecurity)
            failures.Add($"{ConnectionStringsOptions.SectionName}:{DbCredentialManager.AdminConnectionName} precisa de User Id e Password " +
                "(ou Integrated Security) da identidade administrativa dedicada.");
        if (string.IsNullOrWhiteSpace(admin.InitialCatalog))
            failures.Add($"{ConnectionStringsOptions.SectionName}:{DbCredentialManager.AdminConnectionName} precisa de Database=<banco>.");
        if (runtime is not null && !string.IsNullOrWhiteSpace(runtime.InitialCatalog) && !string.IsNullOrWhiteSpace(admin.InitialCatalog) &&
            !string.Equals(runtime.InitialCatalog, admin.InitialCatalog, StringComparison.OrdinalIgnoreCase))
            failures.Add($"{ConnectionStringsOptions.SectionName}:{DbCredentialManager.AdminConnectionName} e almirante precisam apontar para o mesmo banco.");
        if (string.Equals(admin.UserID?.Trim(), appUser.Trim(), StringComparison.OrdinalIgnoreCase))
            failures.Add($"{ConnectionStringsOptions.SectionName}:{DbCredentialManager.AdminConnectionName}: a identidade administrativa não pode ser a de " +
                "runtime (DbCredentials:AppUser); use credenciais distintas.");

        return failures;
    }

    // Lança InvalidOperationException (sem valores de configuração) se a política for violada.
    public static void EnsureValid(IConfiguration configuration)
    {
        var failures = Validate(configuration).ToList();
        if (failures.Count > 0)
            throw new InvalidOperationException("Configuração de identidades SQL recusada: " + string.Join(" ", failures));
    }

    private static SqlConnectionStringBuilder? TryParse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        try { return new SqlConnectionStringBuilder(connectionString); }
        // Malformada: o driver falha ao abrir, com mensagem própria; não é papel desta política.
        catch (Exception) { return null; }
    }
}

// Executa SqlIdentityPolicy junto das demais validações de startup (IStartupValidator), antes de qualquer
// acesso ao banco — inclusive para a CLI reset-admin-password.
public sealed class SqlIdentityPolicyValidator(IConfiguration configuration) : IValidateOptions<ConnectionStringsOptions>
{
    public ValidateOptionsResult Validate(string? name, ConnectionStringsOptions options)
    {
        var failures = SqlIdentityPolicy.Validate(configuration).ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
