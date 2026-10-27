using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Almirante.Api.Data;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

public sealed partial class DbCredentialManager(
    IConfiguration configuration,
    IOptions<DbCredentialOptions> options,
    AppDbCredentialProvider provider,
    ILogger<DbCredentialManager> logger)
{
    public const string AdminConnectionName = "AlmiranteAdmin";
    public const string AppRoleName = "almirante_app_role";
    private const int PasswordLength = 48;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly SemaphoreSlim _lock = new(1, 1);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex IdentifierRegex();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SqlIdentityPolicy.EnsureValid(configuration);
        EnsureValidUserName(options.Value.AppUser);
        var adminConnectionString = GetAdminConnectionString();

        await using (var adminConnection = new SqlConnection(adminConnectionString))
        {
            await OpenAdminAsync(adminConnection, cancellationToken);
            await DbAdminPrivilegeCheck.RunAsync(adminConnection, cancellationToken);
        }

        var adminOptions = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(adminConnectionString).Options;
        await using (var adminDb = new AlmiranteDbContext(adminOptions))
        {
            await DbSeeder.MigrateWithRetryAsync(adminDb, cancellationToken);
        }

        await ExecuteAdminAsync(BuildProvisionSql(options.Value.AppUser!), "conceder as permissões da role de runtime", null, cancellationToken);
        await RotateAsync(cancellationToken);
    }

    public async Task RotateAsync(CancellationToken cancellationToken = default)
    {
        var user = options.Value.AppUser!;
        var password = GeneratePassword();
        var sql = BuildRotateSql(user, password);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            SqlCredential? previous;
            using (provider.BeginSwitch())
            {
                await ExecuteAdminAsync(sql, "rotacionar a senha da identidade de runtime", password, CancellationToken.None);
                previous = provider.Set(user, password);
            }

            ClearPool(previous);
            await VerifyLeastPrivilegeAsync(user, cancellationToken);
            logger.LogInformation("Senha do usuário SQL da aplicação ({User}) rotacionada.", user);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string GetAdminConnectionString() =>
        configuration.GetConnectionString(AdminConnectionName)
        ?? throw new InvalidOperationException($"ConnectionStrings:{AdminConnectionName} é obrigatória quando DbCredentials:AppUser está definido.");

    private string GetRuntimeConnectionString() =>
        configuration.GetConnectionString("almirante")
        ?? throw new InvalidOperationException("ConnectionStrings:almirante é obrigatória.");

    private static void EnsureValidUserName(string? user)
    {
        if (string.IsNullOrEmpty(user) || !IdentifierRegex().IsMatch(user))
            throw new ArgumentException("DbCredentials:AppUser deve conter só letras, dígitos e '_' (máx. 63, sem começar por dígito).", nameof(user));
    }

    private async Task OpenAdminAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try { await connection.OpenAsync(cancellationToken); }
        catch (SqlException exception)
        {
            throw Sanitize(exception, "autenticar a identidade administrativa (ConnectionStrings:AlmiranteAdmin)", null);
        }
    }

    private async Task ExecuteAdminAsync(string sql, string action, string? secret, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(GetAdminConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            throw Sanitize(exception, action, secret);
        }
    }

    private static InvalidOperationException Sanitize(SqlException exception, string action, string? secret)
    {
        var hint = exception.Number switch
        {
            18456 or 4060 => " Confirme SQL_ADMIN_USER/SQL_ADMIN_PASSWORD e execute o bootstrap (docs/sql/criar-usuario-admin-app.sql; no Compose, o serviço sql-bootstrap).",
            15151 or 15247 or 229 or 262 or 297 => " A identidade administrativa não tem a permissão prevista: reaplique o bootstrap (docs/sql/criar-usuario-admin-app.sql).",
            51001 => " Execute o bootstrap (docs/sql/criar-usuario-admin-app.sql; no Compose, o serviço sql-bootstrap).",
            _ => string.Empty,
        };
        var text = $"Falha ao {action} (erro SQL {exception.Number}, estado {exception.State}).{hint}";
        if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return new InvalidOperationException(text);
    }

    private void ClearPool(SqlCredential? previous)
    {
        if (previous is null) return;
        using var probe = new SqlConnection(GetRuntimeConnectionString(), previous);
        SqlConnection.ClearPool(probe);
    }

    public static string BuildRotateSql(string user, string password)
    {
        EnsureValidUserName(user);
        if (password.Length == 0 || password.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Senha gerada inesperada.", nameof(password));
        return $"ALTER USER [{user}] WITH PASSWORD = N'{password}';";
    }

    public static string BuildProvisionSql(string user)
    {
        EnsureValidUserName(user);
        const string role = AppRoleName;
        var auditTablesSql = string.Join(", ", DbPrivilegeAuditor.AuditTables.Select(t => $"N'{t}'"));
        var denyAuditSql = string.Join(" ", DbPrivilegeAuditor.AuditTables.Select(t =>
            $"IF OBJECT_ID(N'dbo.{t}', N'U') IS NOT NULL SET @cmd += N'DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.{t} TO [{role}]; ';"));
        var insertOnlyTablesSql = string.Join(", ", DbPrivilegeAuditor.InsertOnlyTables.Select(t => $"N'{t}'"));
        var insertOnlySql = string.Join(" ", DbPrivilegeAuditor.InsertOnlyTables.Select(t =>
            $"IF OBJECT_ID(N'dbo.{t}', N'U') IS NOT NULL SET @cmd += N'GRANT SELECT, INSERT ON OBJECT::dbo.{t} TO [{role}]; " +
            $"DENY UPDATE, DELETE ON OBJECT::dbo.{t} TO [{role}]; ';"));
        return $"""
            IF DATABASE_PRINCIPAL_ID(N'{user}') IS NULL OR DATABASE_PRINCIPAL_ID(N'{role}') IS NULL
                THROW 51001, N'Identidade de runtime ou role ausente: execute o bootstrap (docs/sql/criar-usuario-admin-app.sql).', 1;

            DECLARE @cmd nvarchar(max) = N'';

            SELECT @cmd += N'REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.' + QUOTENAME(t.name) + N' FROM [{role}]; '
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'dbo');

            SELECT @cmd += N'GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.' + QUOTENAME(t.name) + N' TO [{role}]; '
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name NOT IN ({auditTablesSql}, {insertOnlyTablesSql}, N'__EFMigrationsHistory');

            SELECT @cmd += N'GRANT SELECT ON OBJECT::dbo.' + QUOTENAME(t.name) + N' TO [{role}]; '
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name IN ({auditTablesSql}, N'__EFMigrationsHistory');

            IF OBJECT_ID(N'dbo.{DbPrivilegeAuditor.SessionsTable}', N'U') IS NOT NULL
                SET @cmd += N'GRANT DELETE ON OBJECT::dbo.{DbPrivilegeAuditor.SessionsTable} TO [{role}]; ';
            {denyAuditSql}
            {insertOnlySql}

            EXEC sys.sp_executesql @cmd;
            """;
    }

    private async Task VerifyLeastPrivilegeAsync(string user, CancellationToken cancellationToken)
    {
        var credential = provider.Current ?? throw new InvalidOperationException("Credencial de runtime ausente.");
        try
        {
            await using var connection = new SqlConnection(GetRuntimeConnectionString(), credential);
            await connection.OpenAsync(cancellationToken);
            var findings = (await DbPrivilegeAuditor.FindExcessPrivilegesAsync(connection, cancellationToken)).ToList();
            await using var membership = connection.CreateCommand();
            membership.CommandText = $"SELECT ISNULL(IS_MEMBER(N'{AppRoleName}'), 0)";
            if (Convert.ToInt32(await membership.ExecuteScalarAsync(cancellationToken)) != 1)
                findings.Add($"não é membro de {AppRoleName}");
            if (findings.Count > 0)
            {
                throw new InvalidOperationException(
                    $"O usuário SQL da aplicação ({user}) tem privilégios além (ou aquém) do previsto: {string.Join("; ", findings)}. " +
                    "Reaplique docs/sql/criar-usuario-admin-app.sql como administrador do servidor (no Compose, o serviço sql-bootstrap) e reinicie a API.");
            }
        }
        catch (SqlException exception)
        {
            throw Sanitize(exception, "abrir uma conexão com a credencial de runtime recém-rotacionada", null);
        }
    }

    public static string GeneratePassword()
    {
        while (true)
        {
            var chars = RandomNumberGenerator.GetString(Alphabet, PasswordLength);
            if (chars.Any(char.IsAsciiLetterUpper) && chars.Any(char.IsAsciiLetterLower) && chars.Any(char.IsAsciiDigit))
                return chars;
        }
    }
}
