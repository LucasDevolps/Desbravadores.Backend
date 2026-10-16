using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Tests;

// Ambiente descartável para provar o modelo de identidades SQL contra um SQL Server REAL: um banco novo,
// preparado pelo MESMO script que o operador executa (docs/sql/criar-usuario-admin-app.sql), com a
// identidade administrativa e a de runtime da API. A conexão do "harness" (ALMIRANTE_TEST_SQLSERVER,
// normalmente sysadmin/sa de um SQL Server descartável) só cria e remove o ambiente — é o papel do
// operador/bootstrap. NENHUM código da API sob teste a recebe.
public sealed class SqlIdentityEnvironment : IAsyncDisposable
{
    private readonly List<string> _extraDatabases = [];
    private readonly List<string> _extraLogins = [];

    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Database => $"almirante_id_{Suffix}";
    public string AdminUser => $"almirante_admin_{Suffix}";
    public string AppUser => $"almirante_user_{Suffix}";
    public string AdminPassword { get; private set; } = NewPassword();
    public AppDbCredentialProvider Provider { get; } = new();

    public static string HarnessConnectionString => Environment.GetEnvironmentVariable(SqlServerApiFactory.EnvironmentVariable)
        ?? throw new InvalidOperationException($"Defina {SqlServerApiFactory.EnvironmentVariable}.");

    public static string NewPassword() => $"Tst{Convert.ToHexString(RandomNumberGenerator.GetBytes(20))}aB1";

    private SqlConnectionStringBuilder Target(string database) => new()
    {
        DataSource = new SqlConnectionStringBuilder(HarnessConnectionString).DataSource,
        InitialCatalog = database,
        Encrypt = new SqlConnectionStringBuilder(HarnessConnectionString).Encrypt,
        TrustServerCertificate = new SqlConnectionStringBuilder(HarnessConnectionString).TrustServerCertificate,
    };

    // Connection strings exatamente como o compose as monta: admin com credencial, runtime SEM credencial.
    public string AdminCs => WithAdmin(Target(Database), AdminUser, AdminPassword);
    public string AppCs => Target(Database).ConnectionString;

    public static string WithAdmin(SqlConnectionStringBuilder builder, string user, string password)
    {
        builder.UserID = user;
        builder.Password = password;
        return builder.ConnectionString;
    }

    public IConfiguration Configuration(string? adminCs = null, IDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = AppCs,
            ["ConnectionStrings:AlmiranteAdmin"] = adminCs ?? AdminCs,
            ["DbCredentials:AppUser"] = AppUser,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>()) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public DbCredentialManager NewManager(ILoggerFactory? loggers = null, string? adminCs = null, IDictionary<string, string?>? extra = null) => new(
        Configuration(adminCs, extra),
        Microsoft.Extensions.Options.Options.Create(new DbCredentialOptions { AppUser = AppUser }),
        Provider,
        (loggers ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<DbCredentialManager>());

    // ---- harness (sysadmin do SQL Server descartável) ----

    public static async Task<SqlConnection> OpenHarnessAsync(string database = "master")
    {
        var builder = new SqlConnectionStringBuilder(HarnessConnectionString) { InitialCatalog = database };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task BootstrapAsync(string? adminPassword = null)
    {
        if (adminPassword is not null) AdminPassword = adminPassword;
        await RunScriptAsync(new Dictionary<string, string>
        {
            ["DB_NAME"] = Database, ["ADMIN_USER"] = AdminUser, ["APP_USER"] = AppUser, ["APP_ADMIN_PASSWORD"] = AdminPassword,
        });
    }

    // Executa o arquivo SQL entregue ao operador, com a substituição $(VAR) do sqlcmd e a divisão em GO.
    public static async Task RunScriptAsync(IDictionary<string, string> variables)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "criar-usuario-admin-app.sql"));
        sql = Regex.Replace(sql, @"\$\((\w+)\)", m => variables[m.Groups[1].Value]);
        sql = Regex.Replace(sql, @"^:on error exit\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        await using var connection = await OpenHarnessAsync();
        foreach (var batch in Regex.Split(sql, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await ExecAsync(connection, batch);
    }

    public async Task<string> CreateOtherDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Suffix}";
        _extraDatabases.Add(name);
        await using var connection = await OpenHarnessAsync();
        await ExecAsync(connection, $"CREATE DATABASE [{name}]");
        return name;
    }

    public void TrackLogin(string login) => _extraLogins.Add(login);

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = await OpenHarnessAsync();
        foreach (var database in _extraDatabases.Append(Database))
            await ExecAsync(connection, $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        foreach (var login in _extraLogins.Append(AdminUser).Append(AppUser))
            await ExecAsync(connection, $"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];");
    }

    // ---- utilidades ----

    public async Task<SqlConnection> OpenAdminAsync()
    {
        var connection = new SqlConnection(AdminCs);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<SqlConnection> OpenAppAsync()
    {
        var connection = new SqlConnection(AppCs, Provider.Current);
        await connection.OpenAsync();
        return connection;
    }

    public static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    public static async Task<List<string>> ColumnAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(Convert.ToString(reader.GetValue(0)) ?? "");
        return values;
    }

    // A senha vigente da identidade de runtime, só para os testes provarem que ela nunca vaza (não é logada).
    public string CurrentAppPassword()
    {
        var secure = Provider.Current!.Password;
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try { return Marshal.PtrToStringUni(pointer)!; }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
}

// Captura tudo o que a aplicação loga, para provar que nenhuma senha aparece.
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    public List<string> Entries { get; } = [];
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => new Capture(Entries);
    public void Dispose() { }

    private sealed class Capture(List<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
            lock (entries) entries.Add($"{logLevel} {formatter(state, exception)} {structured} {exception}");
        }
    }
}

// Falha SEM ecoar o segredo: Assert.DoesNotContain imprime a string procurada e o texto inteiro ao falhar, e o que
// se procura aqui é justamente uma senha (descartável, mas nunca deve ir a log de teste/CI).
public static class NoSecret
{
    public static void Assert(string? text, string secret, string where = "saída")
    {
        if (!string.IsNullOrEmpty(secret) && text is not null && text.Contains(secret, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Uma senha vazou na {where} (valor omitido de propósito).");
    }
}
