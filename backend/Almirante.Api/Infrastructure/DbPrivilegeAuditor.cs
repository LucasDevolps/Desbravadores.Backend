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

        // IMPERSONATE em um principal do banco (classe 4) não aparece em nenhuma das checagens acima:
        // é permissão sobre OUTRO usuário/role, não sobre o banco ou um objeto. Com IMPERSONATE em dbo
        // (ou em qualquer membro de db_owner), um EXECUTE AS USER dá privilégio total — por isso entra
        // aqui, e por isso BuildProvisionSql passou a revogar a classe 4.
        findings.AddRange(await PrincipalsAsync(connection, "IMPERSONATE", cancellationToken));

        // EXECUTE por objeto (procedure/função): o teste de escopo de banco acima não alcança um
        // GRANT EXECUTE pontual, e uma procedure com EXECUTE AS OWNER seria um caminho de escalada.
        findings.AddRange(await ModulesAsync(connection, "EXECUTE", cancellationToken));

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

    // Permissão sobre outro principal do banco (securable USER/ROLE), do ponto de vista da identidade
    // conectada. Ignora o próprio usuário (IMPERSONATE em si mesmo é inócuo e sempre verdadeiro).
    private static async Task<IEnumerable<string>> PrincipalsAsync(SqlConnection connection, string permission, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.name FROM sys.database_principals p
            WHERE p.principal_id <> USER_ID()
              AND p.type IN ('S', 'U', 'G', 'R')
              AND p.name NOT IN (N'public', N'guest', N'INFORMATION_SCHEMA', N'sys')
              AND HAS_PERMS_BY_NAME(QUOTENAME(p.name), CASE WHEN p.type = 'R' THEN N'ROLE' ELSE N'USER' END, N'{permission}') = 1
            """;
        return await ReadNamesAsync(command, $"{permission} sobre o principal", cancellationToken);
    }

    // EXECUTE por objeto: procedures e funções do esquema dbo.
    private static async Task<IEnumerable<string>> ModulesAsync(SqlConnection connection, string permission, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT o.name FROM sys.objects o
            WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0
              AND HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), N'OBJECT', N'{permission}') = 1
            """;
        return await ReadNamesAsync(command, $"{permission} em", cancellationToken);
    }

    private static async Task<IEnumerable<string>> ReadNamesAsync(SqlCommand command, string prefix, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add($"{prefix} {reader.GetString(0)}");
        return names;
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

    // Auditoria da conexão ADMINISTRATIVA (ConnectionStrings:AlmiranteAdmin), que é outra coisa: ela
    // precisa de DDL (migrations) e de ALTER ANY LOGIN (rotação da senha do AppUser), então não dá
    // para exigir o mínimo do AppUser aqui. O que ela NÃO deve ser é sysadmin/CONTROL SERVER: com
    // isso, ler o ambiente do container da API entrega o servidor SQL inteiro — inclusive outros
    // bancos e xp_cmdshell — e o trabalho de menor privilégio do AppUser deixa de valer na prática.
    // Ver docs/sql/criar-usuario-admin-app.sql para o login dedicado.
    public static async Task<IReadOnlyList<string>> FindAdminExcessPrivilegesAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();

        foreach (var role in new[] { "sysadmin", "securityadmin", "serveradmin", "setupadmin", "diskadmin", "bulkadmin" })
            if (await ScalarAsync(connection, $"SELECT ISNULL(IS_SRVROLEMEMBER(N'{role}'), 0)", cancellationToken) == 1)
                findings.Add($"papel de servidor {role}");

        foreach (var permission in new[] { "CONTROL SERVER", "IMPERSONATE ANY LOGIN", "ALTER ANY SERVER ROLE" })
            if (await ScalarAsync(connection, $"SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'{permission}'), 0)", cancellationToken) == 1)
                findings.Add($"permissão de servidor {permission}");

        return findings.Distinct().ToList();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }
}

// Política de startup para a conexão ADMINISTRATIVA: Security:AdminPrivilegeCheck = Enforce (recusa
// iniciar), Warn (padrão; só registra) ou Off. O padrão é Warn, e não Enforce, porque a maioria dos
// ambientes existentes ainda usa "sa" aqui — derrubar o startup deles numa atualização seria pior do
// que deixar o aviso visível a cada boot até a migração para o login dedicado.
public static class DbAdminPrivilegeCheck
{
    public const string SettingName = "Security:AdminPrivilegeCheck";

    public static async Task RunAsync(SqlConnection connection, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        var mode = DbPrivilegeCheck.ParseMode(configuration[SettingName], SettingName);
        if (mode == DbPrivilegeCheck.Mode.Off) return;

        var findings = await DbPrivilegeAuditor.FindAdminExcessPrivilegesAsync(connection, cancellationToken);
        if (findings.Count == 0) return;

        var message = $"A conexão administrativa do SQL Server (ConnectionStrings:{DbCredentialManager.AdminConnectionName}) " +
            $"tem privilégio de servidor além do necessário: {string.Join("; ", findings)}. " +
            "Ela só precisa de DDL no banco da aplicação e de ALTER ANY LOGIN. Enquanto for sysadmin, comprometer o " +
            "processo da API equivale a comprometer o servidor SQL inteiro (outros bancos inclusive), o que anula o " +
            "menor privilégio do DbCredentials:AppUser. Crie o login dedicado com docs/sql/criar-usuario-admin-app.sql " +
            $"e aponte a connection string para ele; depois use {SettingName}=Enforce para travar.";

        if (mode == DbPrivilegeCheck.Mode.Enforce) throw new InvalidOperationException(message);
        logger.LogWarning("{Message}", message);
    }
}

// Política de startup para a identidade que NÃO é o login rotacionado (ex.: Windows Authentication do
// IIS): Security:DbPrivilegeCheck = Enforce (recusa iniciar), Warn (padrão; só registra) ou Off. O login
// SQL rotacionado (DbCredentials:AppUser) é sempre Enforce, dentro do DbCredentialManager.
public static class DbPrivilegeCheck
{
    public const string SettingName = "Security:DbPrivilegeCheck";

    public enum Mode { Off, Warn, Enforce }

    public static Mode ParseMode(string? value, string settingName)
    {
        var mode = value?.Trim();
        if (string.IsNullOrEmpty(mode)) return Mode.Warn;
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return Mode.Off;
        if (string.Equals(mode, "Warn", StringComparison.OrdinalIgnoreCase)) return Mode.Warn;
        if (string.Equals(mode, "Enforce", StringComparison.OrdinalIgnoreCase)) return Mode.Enforce;
        throw new InvalidOperationException($"{settingName} deve ser Enforce, Warn ou Off.");
    }

    public static async Task RunAsync(Microsoft.EntityFrameworkCore.DbContext db, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        var mode = ParseMode(configuration[SettingName], SettingName);
        if (mode == Mode.Off) return;

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            var findings = await DbPrivilegeAuditor.FindExcessPrivilegesAsync(connection, cancellationToken);
            if (findings.Count == 0) return;
            var message = $"A identidade com que a API acessa o SQL Server tem privilégios além do mínimo: {string.Join("; ", findings)}. " +
                "Habilite DbCredentials:AppUser (a identidade atual passa a ser só a conexão administrativa de migrations) ou reduza as permissões.";
            if (mode == Mode.Enforce) throw new InvalidOperationException(message);
            logger.LogWarning("{Message}", message);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}
