using System.Security.Cryptography;
using Almirante.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace Almirante.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerAuthFixture>
{
    public const string Name = "SqlServer";
}

// Duas instâncias reais da API (dois hosts, dois pools de DbContext) sobre o MESMO banco SQL Server
// descartável, com migrations reais, rowversion e transações — o que o provider InMemory não prova.
//
// Banco: por padrão sobe um container (Testcontainers, imagem mssql 2022-latest). Para rodar sem
// Docker, defina ALMIRANTE_TESTS_SQLSERVER com uma connection string de servidor (sem Database);
// a fixture cria um banco com nome único e o remove no final.
//
// A configuração vai por variáveis de ambiente porque o AddSqlServerDbContext da Aspire lê a
// connection string antes dos callbacks de ConfigureAppConfiguration do WebApplicationFactory.
// A coleção desabilita paralelismo para que essas variáveis não vazem para outros testes.
public sealed class SqlServerAuthFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin-sql@local.dev";
    public string AdminSenha { get; } = "Sql-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    private MsSqlContainer? _container;
    private string _serverConnectionString = "";
    private readonly string _database = $"almirante_tests_{Guid.NewGuid():N}";
    private readonly Dictionary<string, string?> _previousEnvironment = new();

    public string ConnectionString { get; private set; } = "";
    public WebApplicationFactory<Program> A { get; private set; } = null!;
    public WebApplicationFactory<Program> B { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("ALMIRANTE_TESTS_SQLSERVER");
        if (string.IsNullOrWhiteSpace(external))
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            _serverConnectionString = _container.GetConnectionString();
        }
        else
        {
            _serverConnectionString = external;
        }

        ConnectionString = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = _database,
            TrustServerCertificate = true,
        }.ConnectionString;

        SetEnvironment(new()
        {
            ["ConnectionStrings__almirante"] = ConnectionString,
            ["Jwt__Issuer"] = "Almirante.Api.Tests.SqlServer",
            ["Jwt__Audience"] = "Almirante.Api.Tests.SqlServer",
            ["Jwt__ActiveKeyId"] = "sql-v1",
            ["Jwt__Keys__sql-v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["SeedAdmin__Nome"] = "Administrador SQL",
            ["SeedAdmin__Email"] = AdminEmail,
            ["SeedAdmin__Senha"] = AdminSenha,
        });
        try
        {
            A = Warm(new InstanceFactory());
            B = Warm(new InstanceFactory());
        }
        finally
        {
            RestoreEnvironment();
        }
    }

    public HttpClient CreateClient(WebApplicationFactory<Program> instance) =>
        instance.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });

    public async Task<T> QueryAsync<T>(Func<AlmiranteDbContext, Task<T>> query)
    {
        using var scope = A.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>());
    }

    public async Task DisposeAsync()
    {
        await A.DisposeAsync();
        await B.DisposeAsync();
        SqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    private static WebApplicationFactory<Program> Warm(WebApplicationFactory<Program> factory)
    {
        // Constrói o host agora (migrations + seed), enquanto as variáveis de ambiente estão definidas.
        using var _ = factory.CreateClient();
        return factory;
    }

    private void SetEnvironment(Dictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, value) in _previousEnvironment) Environment.SetEnvironmentVariable(key, value);
    }

    private sealed class InstanceFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
    }
}
