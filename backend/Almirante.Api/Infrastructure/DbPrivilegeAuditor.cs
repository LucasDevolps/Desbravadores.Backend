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

    // Permissões de servidor que nenhuma identidade da aplicação (runtime ou administrativa) pode ter.
    // Usuários contidos nem são principais de servidor, então nunca as têm; a lista existe para provar
    // isso (e para barrar um login de servidor configurado no lugar deles).
    public static readonly string[] ServerPermissions =
        ["CONTROL SERVER", "ALTER ANY LOGIN", "ALTER ANY DATABASE", "CREATE ANY DATABASE", "ALTER ANY SERVER ROLE", "IMPERSONATE ANY LOGIN",
         "ALTER SETTINGS", "SHUTDOWN", "ALTER ANY CREDENTIAL", "ALTER ANY LINKED SERVER", "ALTER ANY CONNECTION", "ADMINISTER BULK OPERATIONS"];

    private static readonly string[] DatabasePermissions =
        ["CONTROL", "ALTER", "ALTER ANY SCHEMA", "ALTER ANY ROLE", "ALTER ANY USER", "CREATE TABLE", "CREATE PROCEDURE",
         "CREATE VIEW", "CREATE SCHEMA", "TAKE OWNERSHIP", "IMPERSONATE ANY LOGIN", "DELETE", "EXECUTE"];

    // Identidade "sa": pelo nome, mas sobretudo pelo SID 0x01 (um "sa" renomeado continua sendo o "sa") e pelo
    // principal_id 1. Recusada independentemente de qualquer configuração.
    public static async Task<bool> IsSaAsync(SqlConnection connection, CancellationToken cancellationToken = default) =>
        await ScalarAsync(connection,
            "SELECT CASE WHEN LOWER(ISNULL(SUSER_SNAME(), N'')) = N'sa' OR SUSER_SID() = 0x01 OR ORIGINAL_LOGIN() = N'sa' THEN 1 ELSE 0 END", cancellationToken) == 1;

    // A conexão já deve estar aberta com a identidade a ser auditada.
    public static async Task<IReadOnlyList<string>> FindExcessPrivilegesAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();

        if (await IsSaAsync(connection, cancellationToken)) findings.Add("identidade sa");

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

    // Auditoria da identidade ADMINISTRATIVA (ConnectionStrings:AlmiranteAdmin), do ponto de vista do próprio
    // SQL Server (privilégios EFETIVOS; o nome do usuário não conta). Ela precisa de DDL e concessões no
    // schema dbo (migrations, GRANT à role de runtime) e de ALTER ANY USER (rotação da senha do usuário de
    // runtime) — e de mais nada. O resultado é sempre fatal no startup (DbAdminPrivilegeCheck).
    //
    // Recusado: ser "sa"; qualquer papel de servidor perigoso; qualquer permissão de servidor perigosa
    // (CONTROL SERVER, ALTER ANY LOGIN, IMPERSONATE ANY LOGIN...); IMPERSONATE sobre outro principal; papéis
    // fixos do banco (db_owner, db_ddladmin, db_securityadmin, db_datareader/writer...) e permissões de
    // banco que ultrapassam o schema dbo (CONTROL/ALTER no banco, ALTER ANY ROLE/SCHEMA, TAKE OWNERSHIP,
    // BACKUP); ser dono do banco; banco TRUSTWORTHY ou com cadeia de propriedade entre bancos ligada (um
    // módulo criado com DDL viraria escalada para o servidor); e qualquer permissão administrativa em OUTRO
    // banco da instância.
    public static async Task<IReadOnlyList<string>> FindAdminExcessPrivilegesAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        var findings = new List<string>();

        if (await IsSaAsync(connection, cancellationToken)) findings.Add("identidade sa");

        foreach (var role in ServerRoles)
            if (await ScalarAsync(connection, $"SELECT ISNULL(IS_SRVROLEMEMBER(N'{role}'), 0)", cancellationToken) == 1)
                findings.Add($"papel de servidor {role}");

        foreach (var permission in ServerPermissions)
            if (await ScalarAsync(connection, $"SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'{permission}'), 0)", cancellationToken) == 1)
                findings.Add($"permissão de servidor {permission}");

        foreach (var role in AdminForbiddenDatabaseRoles)
            if (await ScalarAsync(connection, $"SELECT ISNULL(IS_MEMBER(N'{role}'), 0)", cancellationToken) == 1)
                findings.Add($"papel de banco {role}");

        foreach (var permission in AdminForbiddenDatabasePermissions)
            if (await ScalarAsync(connection, $"SELECT ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'{permission}'), 0)", cancellationToken) == 1)
                findings.Add($"permissão de banco {permission}");

        findings.AddRange(await PrincipalsAsync(connection, "IMPERSONATE", cancellationToken));

        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.databases WHERE database_id = DB_ID() AND owner_sid = SUSER_SID()", cancellationToken) > 0)
            findings.Add("dono do banco da aplicação");
        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.databases WHERE database_id = DB_ID() AND is_trustworthy_on = 1", cancellationToken) > 0)
            findings.Add("banco da aplicação TRUSTWORTHY (módulo com DDL viraria escalada para o servidor)");
        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.databases WHERE database_id = DB_ID() AND is_db_chaining_on = 1", cancellationToken) > 0)
            findings.Add("cadeia de propriedade entre bancos ligada no banco da aplicação");

        // Outros bancos da instância: nenhuma permissão administrativa. (Um usuário contido nem é principal
        // de servidor; isto prova o efeito, inclusive para uma identidade configurada por outro caminho.)
        foreach (var database in await OtherDatabasesAsync(connection, cancellationToken))
            foreach (var permission in OtherDatabasePermissions)
                if (await ScalarAsync(connection, $"SELECT ISNULL(HAS_PERMS_BY_NAME(N'{database.Replace("'", "''")}', N'DATABASE', N'{permission}'), 0)", cancellationToken) == 1)
                    findings.Add($"permissão de banco {permission} em outro banco ({database})");

        return findings.Distinct().ToList();
    }

    // Papéis fixos do banco que a identidade administrativa não usa: as permissões dela são explícitas (CONTROL no
    // schema dbo + DDL + ALTER ANY USER), justamente para não herdar o alcance de um papel fixo.
    private static readonly string[] AdminForbiddenDatabaseRoles =
        ["db_owner", "db_securityadmin", "db_accessadmin", "db_ddladmin", "db_backupoperator", "db_datareader", "db_datawriter"];

    // Permissões de BANCO (escopo do banco inteiro, não do schema dbo) que ela não pode ter. SELECT/INSERT/UPDATE/
    // DELETE/EXECUTE no escopo do banco sairiam de db_datareader/db_datawriter ou de GRANT no banco; as dela vêm
    // do schema dbo, e HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', ...) só é verdadeiro para a concessão no banco.
    private static readonly string[] AdminForbiddenDatabasePermissions =
        ["CONTROL", "ALTER", "ALTER ANY SCHEMA", "ALTER ANY ROLE", "ALTER ANY ASSEMBLY", "TAKE OWNERSHIP", "BACKUP DATABASE", "BACKUP LOG",
         "EXECUTE", "DELETE", "INSERT", "UPDATE", "SELECT"];

    private static readonly string[] OtherDatabasePermissions =
        ["CONTROL", "ALTER", "ALTER ANY USER", "ALTER ANY ROLE", "CREATE TABLE", "TAKE OWNERSHIP", "BACKUP DATABASE", "INSERT", "UPDATE", "DELETE", "EXECUTE"];

    // Bancos online que não são o da aplicação. tempdb fica de fora: toda sessão cria tabelas temporárias nele.
    private static async Task<IReadOnlyList<string>> OtherDatabasesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sys.databases WHERE database_id <> DB_ID() AND database_id <> 2 AND state = 0";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }
}

// Política de startup para a identidade ADMINISTRATIVA. Sem modos: qualquer achado recusa iniciar
// (fail closed). O modo Warn/Off que existiu aqui permitia rodar a API com "sa"; foi removido, e
// SqlIdentityPolicy recusa que a configuração antiga (Security:AdminPrivilegeCheck) volte a valer.
public static class DbAdminPrivilegeCheck
{
    public static async Task RunAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        var findings = await DbPrivilegeAuditor.FindAdminExcessPrivilegesAsync(connection, cancellationToken);
        if (findings.Count == 0) return;

        throw new InvalidOperationException(
            $"A identidade administrativa do SQL Server (ConnectionStrings:{DbCredentialManager.AdminConnectionName}) tem privilégio além do " +
            $"necessário: {string.Join("; ", findings)}. Ela só pode ter DDL e controle do schema dbo do banco da aplicação e ALTER ANY USER " +
            "(migrations, concessões à role de runtime e rotação da senha do usuário de runtime): a API não sobe com sysadmin, 'sa', papel " +
            "de servidor, papel fixo de banco ou acesso a outros bancos. Reaplique docs/sql/criar-usuario-admin-app.sql como administrador " +
            "do servidor (o serviço sql-bootstrap do Compose faz isso) e reinicie a API.");
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

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            // "sa" é recusado em qualquer modo (inclusive Off): a API nunca executa SQL como sa.
            if (await DbPrivilegeAuditor.IsSaAsync(connection, cancellationToken))
                throw new InvalidOperationException(
                    "A API está conectada ao SQL Server como 'sa' (ou como o login de SID 0x01), o que é proibido em qualquer configuração. " +
                    "Use DbCredentials:AppUser + ConnectionStrings:AlmiranteAdmin (identidades criadas por docs/sql/criar-usuario-admin-app.sql).");
            if (mode == Mode.Off) return;

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
