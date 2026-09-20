using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Almirante.Api.Data;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

// Aplica o modelo de duas identidades SQL da API (nenhuma delas é "sa"; ver SqlIdentityPolicy):
//   - ADMINISTRATIVA (ConnectionStrings:AlmiranteAdmin): usuário contido do banco criado por
//     docs/sql/criar-usuario-admin-app.sql. Roda as migrations, concede as tabelas à role de runtime e troca
//     a senha da identidade de runtime. Sem papel de servidor, sem db_owner (DbPrivilegeAuditor prova isso
//     antes de qualquer alteração e recusa o startup se falhar).
//   - RUNTIME (DbCredentials:AppUser): também usuário contido, membro só de almirante_app_role. Permissões:
//     SELECT/INSERT/UPDATE nas tabelas de negócio; DELETE somente em dbo.AuthSessions (limpeza de sessões
//     expiradas — AuthSessionCleanupService; as exclusões de negócio são lógicas); nenhuma escrita direta na
//     auditoria e nenhum DDL. A senha é gerada aqui (CSPRNG), vive só em memória e é rotacionada.
//
// Quem CRIA as identidades e o banco é o bootstrap (fora da API, com o sysadmin do servidor); a API só
// concede/rotaciona. Sem o bootstrap, o startup falha com uma mensagem que diz o que executar.
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

    // Ordem deliberada — nada é alterado antes de a identidade administrativa ser aprovada:
    //   1. política de configuração (sem sa, sem SQL_SA_PASSWORD, admin dedicado obrigatório);
    //   2. auditoria dos privilégios EFETIVOS da identidade administrativa (fail closed);
    //   3. migrations (DDL) pela identidade administrativa;
    //   4. concessões da role de runtime, tabela a tabela;
    //   5. senha nova da identidade de runtime, e conferência do menor privilégio com essa senha.
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

    // Troca a senha da identidade de runtime (ALTER USER ... WITH PASSWORD) e passa a usá-la na hora.
    //
    // Garantias:
    //   - a senha nova é gerada por CSPRNG, nunca aparece em log/exceção e só chega à aplicação depois do
    //     ALTER USER bem-sucedido; se ele falhar, a credencial anterior segue válida e em uso;
    //   - entre o ALTER USER e a publicação da credencial nova, aberturas de conexão esperam (BeginSwitch):
    //     nenhuma abertura NOVA parte da senha que acabou de ser invalidada;
    //   - o pool de conexões da credencial anterior é esvaziado: uma conexão já autenticada com a senha antiga
    //     não é reaproveitada, nem "aceita" a credencial antiga depois da rotação;
    //   - a conferência do menor privilégio roda de novo, agora com a credencial nova.
    // Uma abertura que já tinha lido a credencial antiga e estava no meio do handshake no exato instante do
    // ALTER USER pode falhar uma vez (18456); a requisição correspondente é a única afetada.
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
                // Sem o token de cancelamento: cancelar depois de o servidor aplicar o ALTER USER, mas antes de
                // Set(), deixaria a aplicação com uma senha que o banco já não aceita.
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

    // Executa um lote pela identidade administrativa. A senha (quando há) é literal do SQL — ALTER USER não
    // aceita parâmetro —, então nada do SqlException original (que poderia citar trechos do lote) sai daqui.
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

    // Só o número/estado do erro do SQL Server e um roteiro; a mensagem original pode ecoar parte do lote.
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

    // Esvazia o pool da credencial que acabou de ser substituída (ver RotateAsync).
    private void ClearPool(SqlCredential? previous)
    {
        if (previous is null) return;
        using var probe = new SqlConnection(GetRuntimeConnectionString(), previous);
        SqlConnection.ClearPool(probe);
    }

    // Identificadores validados por regex e senha só alfanumérica (sem aspas): CREATE/ALTER USER ... WITH
    // PASSWORD não aceitam parâmetro para a senha.
    public static string BuildRotateSql(string user, string password)
    {
        EnsureValidUserName(user);
        if (password.Length == 0 || password.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Senha gerada inesperada.", nameof(password));
        return $"ALTER USER [{user}] WITH PASSWORD = N'{password}';";
    }

    // Permissões da role de runtime, tabela a tabela (nada no schema inteiro, nenhum papel fixo):
    //   - SELECT/INSERT/UPDATE nas tabelas de negócio; DELETE só em AuthSessions (limpeza de sessões);
    //   - somente SELECT em __EFMigrationsHistory e em lancamentos_deletados, com DENY de escrita nesta
    //     última: o histórico de exclusões só é gravado pelo trigger (cadeia de propriedade dbo), nunca
    //     diretamente pela identidade da aplicação.
    // Recriadas a cada startup (REVOKE + GRANT) — só no startup, com a API ainda sem tráfego: a rotação
    // periódica troca apenas a senha e nunca mexe em permissões.
    // Usuário/role e a normalização de papéis vêm do bootstrap (a identidade administrativa não tem, de
    // propósito, ALTER ANY ROLE nem CONTROL no banco); um desvio ali é pego por VerifyLeastPrivilegeAsync.
    public static string BuildProvisionSql(string user)
    {
        EnsureValidUserName(user);
        const string role = AppRoleName;
        return $"""
            IF DATABASE_PRINCIPAL_ID(N'{user}') IS NULL OR DATABASE_PRINCIPAL_ID(N'{role}') IS NULL
                THROW 51001, N'Identidade de runtime ou role ausente: execute o bootstrap (docs/sql/criar-usuario-admin-app.sql).', 1;

            DECLARE @cmd nvarchar(max) = N'';

            SELECT @cmd += N'REVOKE SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.' + QUOTENAME(t.name) + N' FROM [{role}]; '
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(N'dbo');

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

    // Prova, com a senha recém-rotacionada e do ponto de vista do próprio SQL Server, que a identidade de
    // runtime não tem privilégio excessivo. Falha (sem segredos na mensagem) em vez de operar com uma
    // identidade que o bootstrap deixou mais privilegiada do que deveria.
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
            // CHECK_POLICY exige complexidade: ao menos maiúscula, minúscula e dígito.
            if (chars.Any(char.IsAsciiLetterUpper) && chars.Any(char.IsAsciiLetterLower) && chars.Any(char.IsAsciiDigit))
                return chars;
        }
    }
}
