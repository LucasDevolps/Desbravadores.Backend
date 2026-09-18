using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

public class DbCredentialManagerUnitTests
{
    [Fact]
    public void GeneratePassword_HasComplexityAndIsRandom()
    {
        var a = DbCredentialManager.GeneratePassword();
        var b = DbCredentialManager.GeneratePassword();

        Assert.NotEqual(a, b);
        Assert.Equal(48, a.Length);
        Assert.Contains(a, char.IsAsciiLetterUpper);
        Assert.Contains(a, char.IsAsciiLetterLower);
        Assert.Contains(a, char.IsAsciiDigit);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Theory]
    [InlineData("x]; DROP LOGIN sa;--")]
    [InlineData("1abc")]
    [InlineData("")]
    [InlineData("com espaco")]
    public void BuildProvisionSql_RejectsUnsafeUserNames(string user) =>
        Assert.Throws<ArgumentException>(() => DbCredentialManager.BuildProvisionSql(user, "Abc123"));

    [Fact]
    public void BuildProvisionSql_RejectsNonAlphanumericPassword() =>
        Assert.Throws<ArgumentException>(() => DbCredentialManager.BuildProvisionSql("almirante_user_bd", "abc'; --"));

    [Fact]
    public void BuildProvisionSql_GrantsOnlySelectInsertUpdateAndDeleteOnlyOnAuthSessions()
    {
        var sql = DbCredentialManager.BuildProvisionSql("almirante_user_bd", "Abc123def");

        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON SCHEMA::dbo TO [almirante_user_bd]", sql);
        Assert.Contains("GRANT DELETE ON OBJECT::dbo.AuthSessions TO [almirante_user_bd]", sql);
        Assert.DoesNotContain("db_owner", sql);
        Assert.DoesNotContain("db_datawriter", sql);
    }
}

// Exige SQL Server real com login sysadmin (ver SqlServerApiFactory.EnvironmentVariable). Cria um banco
// e um login descartáveis; ambos são removidos ao final.
[Trait("Category", "RequiresSqlServer")]
public class DbCredentialManagerSqlServerTests
{
    [Fact]
    public async Task InitializeAndRotate_ProvisionsLeastPrivilegeUserAndInvalidatesOldPassword()
    {
        var server = Environment.GetEnvironmentVariable(SqlServerApiFactory.EnvironmentVariable)
            ?? throw new InvalidOperationException($"Defina {SqlServerApiFactory.EnvironmentVariable}.");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var database = $"almirante_cred_{suffix}";
        var appUser = $"almirante_user_{suffix}";
        var adminCs = new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString;
        var adminBuilder = new SqlConnectionStringBuilder(adminCs);
        var appCs = new SqlConnectionStringBuilder
        {
            DataSource = adminBuilder.DataSource,
            InitialCatalog = database,
            TrustServerCertificate = adminBuilder.TrustServerCertificate,
            Encrypt = adminBuilder.Encrypt,
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = appCs.ConnectionString,
            ["ConnectionStrings:AlmiranteAdmin"] = adminCs,
        }).Build();
        var provider = new AppDbCredentialProvider();
        var manager = new DbCredentialManager(configuration, Microsoft.Extensions.Options.Options.Create(new DbCredentialOptions { AppUser = appUser }), provider, NullLogger<DbCredentialManager>.Instance);

        try
        {
            await manager.InitializeAsync();
            var first = provider.Current!;

            await using (var app = new SqlConnection(appCs.ConnectionString, first))
            {
                await app.OpenAsync();
                Assert.Equal(1, await Scalar(app, "SELECT HAS_PERMS_BY_NAME('dbo.Cargos','OBJECT','SELECT')"));
                Assert.Equal(1, await Scalar(app, "SELECT HAS_PERMS_BY_NAME('dbo.Cargos','OBJECT','INSERT')"));
                Assert.Equal(1, await Scalar(app, "SELECT HAS_PERMS_BY_NAME('dbo.Cargos','OBJECT','UPDATE')"));
                Assert.Equal(0, await Scalar(app, "SELECT HAS_PERMS_BY_NAME('dbo.Cargos','OBJECT','DELETE')"));
                Assert.Equal(0, await Scalar(app, "SELECT HAS_PERMS_BY_NAME('dbo.Lancamentos','OBJECT','DELETE')"));
                Assert.Equal(1, await Scalar(app, "SELECT HAS_PERMS_BY_NAME('dbo.AuthSessions','OBJECT','DELETE')"));
                Assert.Equal(0, await Scalar(app, "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE')"));
                Assert.Equal(0, await Scalar(app, "SELECT HAS_PERMS_BY_NAME('dbo.Cargos','OBJECT','ALTER')"));
            }

            await manager.RotateAsync();
            var second = provider.Current!;

            await using (var app = new SqlConnection(appCs.ConnectionString, second))
            {
                await app.OpenAsync();
            }

            SqlConnection.ClearAllPools();
            await using var stale = new SqlConnection(appCs.ConnectionString, first);
            var ex = await Assert.ThrowsAsync<SqlException>(() => stale.OpenAsync());
            Assert.Equal(18456, ex.Number);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            var master = new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString;
            await using var connection = new SqlConnection(master);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END
                IF SUSER_ID(N'{appUser}') IS NOT NULL DROP LOGIN [{appUser}];
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<int> Scalar(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
