using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Almirante.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Executa o arquivo SQL entregue ao operador, com banco/login exclusivos por teste.
// Nenhum teste cria, altera ou apaga o adm-ti de um ambiente existente.
[Trait("Category", "RequiresSqlServer")]
[Collection("AdmTiPrivilege")]
public sealed class AdmTiPrivilegeTests : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly string _server = Environment.GetEnvironmentVariable(SqlServerApiFactory.EnvironmentVariable)
        ?? throw new InvalidOperationException($"Defina {SqlServerApiFactory.EnvironmentVariable}.");
    private string Database => $"almirante_ti_{_suffix}";
    private string Login => $"adm_ti_{_suffix}";
    private string Password { get; set; } = NewPassword();
    private string AdminCs => new SqlConnectionStringBuilder(_server) { InitialCatalog = Database }.ConnectionString;
    private static string NewPassword() => $"Tst-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24))}-Aa1!";

    public async Task InitializeAsync()
    {
        await using var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(AdminCs).Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, $"""
            IF DB_ID(N'{Database}') IS NOT NULL BEGIN ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; END
            IF SUSER_ID(N'{Login}') IS NOT NULL DROP LOGIN [{Login}];
            """);
    }

    private async Task<SqlConnection> OpenAdminAsync()
    {
        var connection = new SqlConnection(AdminCs);
        await connection.OpenAsync();
        return connection;
    }

    private async Task RunScriptAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "criar-usuario-adm-ti.sql"));
        // Substituições limitadas a identificadores gerados (hex) e senha sem aspas, como no sqlcmd.
        sql = sql.Replace("adm-ti", Login).Replace("$(DB_NAME)", Database).Replace("$(ADM_TI_PASSWORD)", Password);
        sql = Regex.Replace(sql, @"^:on error exit\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        await using var connection = await OpenAdminAsync();
        try
        {
            foreach (var batch in Regex.Split(sql, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await ExecAsync(connection, batch);
        }
        catch
        {
            // Equivalente ao :on error exit do sqlcmd: nenhum batch seguinte é executado;
            // a conexão do operador é encerrada e sua transação pendente é desfeita.
            await ExecAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            throw;
        }
    }

    [Fact]
    public async Task LoginNovo_EReexecucao_PermitemSoAsViewsSemCredenciais()
    {
        await RunScriptAsync();
        await AssertLeastPrivilegeAsync();
        Password = NewPassword();
        await RunScriptAsync();
        await AssertLeastPrivilegeAsync();
    }

    [Fact]
    public async Task LoginLegado_RemoveRolesPermissoesDeServidorImpersonacaoEGrantDeColuna()
    {
        await RunScriptAsync();
        await using (var admin = await OpenAdminAsync())
        {
            await ExecAsync(admin, $"""
                ALTER SERVER ROLE [sysadmin] ADD MEMBER [{Login}];
                GRANT CONTROL SERVER TO [{Login}];
                GRANT IMPERSONATE ON LOGIN::[sa] TO [{Login}];
                ALTER ROLE [db_owner] ADD MEMBER [{Login}];
                ALTER ROLE [db_datareader] ADD MEMBER [almirante_ti_leitura];
                GRANT IMPERSONATE ON USER::[dbo] TO [{Login}];
                GRANT IMPERSONATE ON USER::[dbo] TO [almirante_ti_leitura];
                GRANT SELECT ON OBJECT::dbo.Usuarios (SenhaHash) TO [{Login}];
                """);
        }

        Password = NewPassword();
        await RunScriptAsync();
        await AssertLeastPrivilegeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GrantOption_AbortaERollbackPreservaEstadoParaRevisaoManual(bool serverScope)
    {
        await RunScriptAsync();
        await using (var admin = await OpenAdminAsync())
        {
            await ExecAsync(admin, serverScope
                ? $"GRANT VIEW SERVER STATE TO [{Login}] WITH GRANT OPTION;"
                : $"GRANT IMPERSONATE ON USER::[dbo] TO [{Login}] WITH GRANT OPTION;");
        }

        var previousPassword = Password;
        Password = NewPassword();
        var ex = await Assert.ThrowsAsync<SqlException>(RunScriptAsync);
        Assert.Equal(51000, ex.Number);
        Assert.Contains("GRANT OPTION", ex.Message);
        Assert.DoesNotContain(Password, ex.Message);
        Password = previousPassword;

        // A falha não publica a senha nova nem retorna sucesso parcial.
        await using var connection = await OpenTiAsync();
        Assert.Equal(1, await ScalarAsync(connection, "SELECT 1"));
    }

    [Fact]
    public async Task ProprietarioDeSchema_AbortaEmVezDeDeclararMenorPrivilegio()
    {
        await RunScriptAsync();
        await using (var admin = await OpenAdminAsync())
            await ExecAsync(admin, $"CREATE SCHEMA [legado] AUTHORIZATION [{Login}];");

        var ex = await Assert.ThrowsAsync<SqlException>(RunScriptAsync);
        Assert.Equal(51000, ex.Number);
        Assert.Contains("propriedade", ex.Message);
    }

    private async Task AssertLeastPrivilegeAsync()
    {
        await using var connection = await OpenTiAsync();
        Assert.Equal(0, await ScalarAsync(connection, "SELECT IS_SRVROLEMEMBER('sysadmin')"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER')"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT IS_MEMBER('db_owner')"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT HAS_PERMS_BY_NAME('dbo', 'USER', 'IMPERSONATE')"));

        foreach (var view in new[] { "Usuarios", "Cargos", "Lancamentos", "lancamentos_deletados", "AuthSessions" })
            await ExecAsync(connection, $"SELECT TOP (1) * FROM ti.[{view}];");

        foreach (var sql in new[]
        {
            "SELECT SenhaHash FROM dbo.Usuarios;",
            "SELECT TokenHash FROM dbo.RefreshTokens;",
            "UPDATE dbo.Usuarios SET Nome = N'adulterado' WHERE 1 = 0;",
            "UPDATE ti.Usuarios SET Nome = N'adulterado' WHERE 1 = 0;",
            "EXECUTE AS USER = N'dbo';",
            "EXECUTE AS LOGIN = N'sa';",
            "CREATE TABLE dbo.NaoPermitida (Id int);",
        })
            await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection, sql));

        // A view não expõe os campos secretos, independentemente de haver registros nas tabelas.
        Assert.Equal(0, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id IN (OBJECT_ID(N'ti.Usuarios'), OBJECT_ID(N'ti.AuthSessions'))
              AND name IN (N'SenhaHash', N'TokenHash');
            """));
    }

    private async Task<SqlConnection> OpenTiAsync()
    {
        var server = new SqlConnectionStringBuilder(_server);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.DataSource, InitialCatalog = Database, UserID = Login, Password = Password,
            Encrypt = server.Encrypt, TrustServerCertificate = server.TrustServerCertificate, Pooling = false,
        };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}

// O script examina os outros bancos do servidor. Evita que outra fixture descarte um banco
// entre a enumeração do catálogo e a consulta, mantendo os demais testes livres para paralelizar.
[CollectionDefinition("AdmTiPrivilege", DisableParallelization = true)]
public sealed class AdmTiPrivilegeCollection { }
