using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Almirante.Api.Data;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

// Provisiona e rotaciona o login SQL da aplicação usando a conexão administrativa
// (ConnectionStrings:AlmiranteAdmin). Permissões do AppUser: SELECT/INSERT/UPDATE no schema dbo e
// DELETE somente em dbo.AuthSessions (limpeza de sessões expiradas — AuthSessionCleanupService);
// as exclusões de negócio são lógicas, então nenhuma outra tabela recebe DELETE nem DDL.
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

    // Migrations exigem DDL, que o AppUser não tem: rodam pela conexão administrativa, antes de o
    // login da aplicação ser provisionado.
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var appConnection = new SqlConnectionStringBuilder(configuration.GetConnectionString("almirante"));
        if (!string.IsNullOrEmpty(appConnection.UserID) || !string.IsNullOrEmpty(appConnection.Password) || appConnection.IntegratedSecurity)
        {
            throw new InvalidOperationException(
                "ConnectionStrings:almirante não pode conter User Id/Password/Integrated Security quando DbCredentials:AppUser está definido " +
                "(a credencial do usuário da aplicação é injetada em tempo de execução).");
        }

        var adminOptions = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(GetAdminConnectionString()).Options;
        await using (var adminDb = new AlmiranteDbContext(adminOptions))
        {
            await DbSeeder.MigrateWithRetryAsync(adminDb, cancellationToken);
        }

        await RotateAsync(cancellationToken);
    }

    // Cria (ou reaproveita) o login/usuário, reaplica as permissões e troca a senha por uma nova
    // aleatória. A senha só passa a valer para a API depois do ALTER LOGIN bem-sucedido; se este
    // método falhar antes disso, a credencial anterior segue válida e em uso.
    public async Task RotateAsync(CancellationToken cancellationToken = default)
    {
        var user = options.Value.AppUser!;
        var password = GeneratePassword();
        var sql = BuildProvisionSql(user, password);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqlConnection(GetAdminConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            provider.Set(user, password);
            await connection.CloseAsync();
            await VerifyLeastPrivilegeAsync(user, password, cancellationToken);
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

    // Identificadores validados por regex e senha só alfanumérica (sem aspas) — necessário porque
    // CREATE/ALTER LOGIN não aceitam parâmetros para a senha.
    //
    // Privilégios: a aplicação não recebe permissão no esquema inteiro nem papéis fixos do banco. Tudo vai
    // para uma role própria (AppRoleName), com GRANT explícito por tabela, recriado a cada execução:
    //   - SELECT/INSERT/UPDATE nas tabelas de negócio; DELETE só em AuthSessions (limpeza de sessões);
    //   - somente SELECT em __EFMigrationsHistory e em lancamentos_deletados, com DENY de escrita nesta
    //     última: o histórico de exclusões só é gravado pelo trigger (cadeia de propriedade dbo), nunca
    //     diretamente pela identidade da aplicação.
    // Um login pré-existente (ex.: provisionado por versões anteriores como db_owner) é normalizado:
    // sai de qualquer outra role do banco e perde permissões diretas, exceto CONNECT.
    public static string BuildProvisionSql(string user, string password)
    {
        if (!IdentifierRegex().IsMatch(user))
            throw new ArgumentException("DbCredentials:AppUser deve conter só letras, dígitos e '_' (máx. 63, sem começar por dígito).", nameof(user));
        if (password.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Senha gerada inesperada.", nameof(password));

        const string role = AppRoleName;
        return $"""
            IF SUSER_ID(N'{user}') IS NULL
                CREATE LOGIN [{user}] WITH PASSWORD = N'{password}', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
            ELSE
                ALTER LOGIN [{user}] WITH PASSWORD = N'{password}';

            IF DATABASE_PRINCIPAL_ID(N'{user}') IS NULL
                CREATE USER [{user}] FOR LOGIN [{user}] WITH DEFAULT_SCHEMA = [dbo];
            ELSE
                ALTER USER [{user}] WITH LOGIN = [{user}];

            IF DATABASE_PRINCIPAL_ID(N'{role}') IS NULL
                CREATE ROLE [{role}];

            DECLARE @cmd nvarchar(max) = N'';

            -- Normaliza o usuário: fora de qualquer role que não seja a da aplicação (papéis fixos do banco).
            SELECT @cmd += N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(u.name) + N'; '
            FROM sys.database_role_members m
            JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
            JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
            WHERE u.name = N'{user}' AND r.name <> N'{role}';

            -- Revoga permissões concedidas diretamente ao usuário e as anteriores da própria role (recriadas abaixo).
            SELECT @cmd += N'REVOKE ' + p.permission_name COLLATE DATABASE_DEFAULT
                + CASE p.class
                    WHEN 0 THEN N''
                    WHEN 1 THEN N' ON OBJECT::' + QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(p.major_id))
                    WHEN 3 THEN N' ON SCHEMA::' + QUOTENAME(SCHEMA_NAME(p.major_id))
                  END
                + N' FROM ' + QUOTENAME(g.name) + N'; '
            FROM sys.database_permissions p
            JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
            WHERE g.name IN (N'{user}', N'{role}') AND p.permission_name <> N'CONNECT' AND p.class IN (0, 1, 3) AND p.minor_id = 0;

            IF IS_ROLEMEMBER(N'{role}', N'{user}') = 0
                SET @cmd += N'ALTER ROLE [{role}] ADD MEMBER [{user}]; ';

            SELECT @cmd += N'GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.' + QUOTENAME(t.name) + N' TO [{role}]; '
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name NOT IN (N'{DbPrivilegeAuditor.AuditTable}', N'__EFMigrationsHistory');

            SELECT @cmd += N'GRANT SELECT ON OBJECT::dbo.' + QUOTENAME(t.name) + N' TO [{role}]; '
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name IN (N'{DbPrivilegeAuditor.AuditTable}', N'__EFMigrationsHistory');

            IF OBJECT_ID(N'dbo.{DbPrivilegeAuditor.SessionsTable}', N'U') IS NOT NULL
                SET @cmd += N'GRANT DELETE ON OBJECT::dbo.{DbPrivilegeAuditor.SessionsTable} TO [{role}]; ';
            IF OBJECT_ID(N'dbo.{DbPrivilegeAuditor.AuditTable}', N'U') IS NOT NULL
                SET @cmd += N'DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.{DbPrivilegeAuditor.AuditTable} TO [{role}]; ';

            EXEC sys.sp_executesql @cmd;
            """;
    }

    // Prova, com a senha recém-rotacionada e do ponto de vista do próprio SQL Server, que a identidade da
    // aplicação não tem privilégio excessivo. Falha o startup (sem segredos na mensagem) em vez de operar
    // com um login privilegiado que o provisionamento não conseguiu normalizar (ex.: papel de servidor).
    private async Task VerifyLeastPrivilegeAsync(string user, string password, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(configuration.GetConnectionString("almirante"));
        await using var connection = new SqlConnection(builder.ConnectionString, provider.Current ?? new SqlCredential(user, ToSecure(password)));
        await connection.OpenAsync(cancellationToken);
        var findings = await DbPrivilegeAuditor.FindExcessPrivilegesAsync(connection, cancellationToken);
        if (findings.Count > 0)
        {
            throw new InvalidOperationException(
                $"O usuário SQL da aplicação ({user}) tem privilégios além do mínimo necessário: {string.Join("; ", findings)}. " +
                "Corrija com docs/sql/corrigir-privilegios-usuario-app.sql (executado como administrador) e reinicie a API.");
        }
    }

    private static System.Security.SecureString ToSecure(string value)
    {
        var secure = new System.Security.SecureString();
        foreach (var c in value) secure.AppendChar(c);
        secure.MakeReadOnly();
        return secure;
    }

    public static string GeneratePassword()
    {
        while (true)
        {
            var chars = RandomNumberGenerator.GetString(Alphabet, PasswordLength);
            // CHECK_POLICY exige complexidade: ao menos maiúscula, minúscula e dígito.
            if (chars.Any(char.IsAsciiLetterUpper) && chars.Any(char.IsAsciiLetterLower) && chars.Any(char.IsAsciiDigit))
                return chars;
        }
    }
}
