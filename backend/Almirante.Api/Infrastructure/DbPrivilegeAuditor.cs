using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Infrastructure;

// Confere as permissões EFETIVAS da identidade com que a aplicação se conecta ao SQL Server (login SQL
// da aplicação ou identidade Windows do IIS), do ponto de vista do próprio SQL Server: papéis fixos de
// servidor/banco (inclusive por grupo Windows), permissões diretas ou herdadas, DELETE arbitrário,
// escrita na tabela de auditoria e propriedade de esquemas/objetos. Só devolve códigos fixos — nunca
// connection string nem senha — para poderem ir a log/exceção de startup.
public static class DbPrivilegeAuditor
{
    public const string AuditTable = "lancamentos_deletados";
    public const string SessionsTable = "AuthSessions";

    private static readonly string[] ServerRoles =
        ["sysadmin", "securityadmin", "serveradmin", "setupadmin", "processadmin", "diskadmin", "dbcreator", "bulkadmin"];

    private static readonly string[] DatabaseRoles =
        ["db_owner", "db_securityadmin", "db_accessadmin", "db_backupoperator", "db_ddladmin", "db_datawriter"];

    private static readonly string[] ServerPermissions = ["CONTROL SERVER", "ALTER ANY LOGIN", "ALTER ANY DATABASE", "ALTER ANY SERVER ROLE"];

    private static readonly string[] DatabasePermissions =
        ["CONTROL", "ALTER", "ALTER ANY SCHEMA", "ALTER ANY ROLE", "ALTER ANY USER", "CREATE TABLE", "CREATE PROCEDURE",
         "CREATE VIEW", "CREATE SCHEMA", "TAKE OWNERSHIP", "IMPERSONATE ANY LOGIN", "DELETE", "EXECUTE"];

    // A conexão já deve estar aberta com a identidade a ser auditada.
    public static async Task<IReadOnlyList<string>> FindExcessPrivilegesAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();

        foreach (var role in ServerRoles)
            if (await ScalarAsync(connection, $"SELECT ISNULL(IS_SRVROLEMEMBER(N'{role}'), 0)", cancellationToken) == 1)
                findings.Add($"papel de servidor {role}");

        foreach (var permission in ServerPermissions)
            if (await ScalarAsync(connection, $"SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'{permission}'), 0)", cancellationToken) == 1)
                findings.Add($"permissão de servidor {permission}");

        foreach (var role in DatabaseRoles)
            if (await ScalarAsync(connection, $"SELECT ISNULL(IS_MEMBER(N'{role}'), 0)", cancellationToken) == 1)
                findings.Add($"papel de banco {role}");

        foreach (var permission in DatabasePermissions)
            if (await ScalarAsync(connection, $"SELECT ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'{permission}'), 0)", cancellationToken) == 1)
                findings.Add($"permissão de banco {permission}");

        // Exclusão física só é aceitável em AuthSessions (limpeza de sessões expiradas); a auditoria não
        // pode ser escrita diretamente — só o trigger (cadeia de propriedade) grava nela.
        findings.AddRange(await TablesAsync(connection, "DELETE", $"t.name <> N'{SessionsTable}'", "DELETE em", cancellationToken));
        foreach (var permission in new[] { "INSERT", "UPDATE", "DELETE" })
            findings.AddRange(await TablesAsync(connection, permission, $"t.name = N'{AuditTable}'", $"{permission} direto na tabela de auditoria", cancellationToken));
        foreach (var permission in new[] { "ALTER", "CONTROL", "REFERENCES", "TAKE OWNERSHIP" })
            findings.AddRange(await TablesAsync(connection, permission, "1 = 1", $"{permission} em", cancellationToken));

        var owned = await ScalarAsync(connection,
            "SELECT (SELECT COUNT(*) FROM sys.schemas WHERE principal_id = USER_ID()) + (SELECT COUNT(*) FROM sys.objects WHERE principal_id = USER_ID())", cancellationToken);
        if (owned > 0) findings.Add("proprietário de esquema/objetos do banco");

        return findings.Distinct().ToList();
    }

    private static async Task<IEnumerable<string>> TablesAsync(SqlConnection connection, string permission, string filter, string prefix, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.name FROM sys.tables t
            WHERE {filter} AND HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name), N'OBJECT', N'{permission}') = 1
            """;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add($"{prefix} {reader.GetString(0)}");
        return names;
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }
}

// Política de startup para a identidade que NÃO é o login rotacionado (ex.: Windows Authentication do
// IIS): Security:DbPrivilegeCheck = Enforce (recusa iniciar), Warn (padrão; só registra) ou Off. O login
// SQL rotacionado (DbCredentials:AppUser) é sempre Enforce, dentro do DbCredentialManager.
public static class DbPrivilegeCheck
{
    public const string SettingName = "Security:DbPrivilegeCheck";

    public static async Task RunAsync(Microsoft.EntityFrameworkCore.DbContext db, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        var mode = configuration[SettingName]?.Trim() ?? "Warn";
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(mode, "Warn", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "Enforce", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SettingName} deve ser Enforce, Warn ou Off.");

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            var findings = await DbPrivilegeAuditor.FindExcessPrivilegesAsync(connection, cancellationToken);
            if (findings.Count == 0) return;
            var message = $"A identidade com que a API acessa o SQL Server tem privilégios além do mínimo: {string.Join("; ", findings)}. " +
                "Habilite DbCredentials:AppUser (a identidade atual passa a ser só a conexão administrativa de migrations) ou reduza as permissões.";
            if (string.Equals(mode, "Enforce", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(message);
            logger.LogWarning("{Message}", message);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}
