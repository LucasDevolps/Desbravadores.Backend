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

    // Identificador validado por regex e senha só alfanumérica (sem aspas) — necessário porque
    // CREATE/ALTER LOGIN não aceitam parâmetros para a senha.
    public static string BuildProvisionSql(string user, string password)
    {
        if (!IdentifierRegex().IsMatch(user))
            throw new ArgumentException("DbCredentials:AppUser deve conter só letras, dígitos e '_' (máx. 63, sem começar por dígito).", nameof(user));
        if (password.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Senha gerada inesperada.", nameof(password));

        return $"""
            IF SUSER_ID(N'{user}') IS NULL
                CREATE LOGIN [{user}] WITH PASSWORD = N'{password}', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
            ELSE
                ALTER LOGIN [{user}] WITH PASSWORD = N'{password}';

            IF DATABASE_PRINCIPAL_ID(N'{user}') IS NULL
                CREATE USER [{user}] FOR LOGIN [{user}] WITH DEFAULT_SCHEMA = [dbo];
            ELSE
                ALTER USER [{user}] WITH LOGIN = [{user}];

            GRANT SELECT, INSERT, UPDATE ON SCHEMA::dbo TO [{user}];
            REVOKE DELETE, ALTER, CONTROL ON SCHEMA::dbo FROM [{user}];
            IF OBJECT_ID(N'dbo.AuthSessions', N'U') IS NOT NULL
                GRANT DELETE ON OBJECT::dbo.AuthSessions TO [{user}];
            """;
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
