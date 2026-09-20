using Microsoft.Data.SqlClient;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

// Startup REAL da API (processo dotnet Almirante.Api.dll, como no container). Os testes sem SQL Server provam a
// recusa técnica de "sa" e do segredo dele, e a ausência de fallback; os com SQL Server provam a subida com a
// identidade administrativa dedicada, as operações normais pela identidade de runtime e o restart sem "sa".
public class ApiProcessRefusalTests
{
    private const string NaoExiste = "Server=127.0.0.1,1;Database=almirante;Encrypt=True;TrustServerCertificate=True";

    private static Dictionary<string, string> Config(string? admin, string? saSecretName = null)
    {
        var env = ApiProcess.BaseEnvironment();
        env["ConnectionStrings__almirante"] = NaoExiste;
        env["DbCredentials__AppUser"] = "almirante_user_bd";
        if (admin is not null) env["ConnectionStrings__AlmiranteAdmin"] = admin;
        if (saSecretName is not null) env[saSecretName] = "ValorSecretoDoSa-987";
        return env;
    }

    private static string Admin(string user) =>
        $"Server=127.0.0.1,1;Database=almirante;User Id={user};Password=SenhaAdmin-Nao-Usada-123;Encrypt=True;TrustServerCertificate=True";

    private static async Task<string> RefusedAsync(Dictionary<string, string> env)
    {
        await using var api = ApiProcess.Start(env);
        var (started, exitCode) = await api.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.False(started, "a API não pode subir com esta configuração");
        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("SenhaAdmin-Nao-Usada-123", api.Output);
        Assert.DoesNotContain("ValorSecretoDoSa-987", api.Output);
        return api.Output;
    }

    [Theory]
    [InlineData("sa")]
    [InlineData("SA")]
    public async Task Api_RecusaIniciarComSaComoIdentidadeAdministrativa(string user)
    {
        var saida = await RefusedAsync(Config(Admin(user)));

        Assert.Contains("'sa'", saida);
        Assert.Contains("ConnectionStrings:AlmiranteAdmin", saida);
    }

    [Fact]
    public async Task Api_RecusaIniciarComSaNaConnectionStringDeRuntimeSemDbCredentials()
    {
        var env = ApiProcess.BaseEnvironment();
        env["ConnectionStrings__almirante"] = "Server=127.0.0.1,1;Database=almirante;User Id=sa;Password=SenhaAdmin-Nao-Usada-123;Encrypt=True;TrustServerCertificate=True";

        Assert.Contains("'sa'", await RefusedAsync(env));
    }

    [Theory]
    [InlineData("SQL_SA_PASSWORD")]
    [InlineData("MSSQL_SA_PASSWORD")]
    public async Task Api_RecusaIniciarSeORecebeOSegredoDoSaNoAmbiente(string nome)
    {
        var saida = await RefusedAsync(Config(Admin("almirante_admin_bd"), nome));

        Assert.Contains(nome, saida);
    }

    [Fact]
    public async Task Api_SemIdentidadeAdministrativa_NaoTemFallbackParaSa()
    {
        var saida = await RefusedAsync(Config(admin: null));

        Assert.Contains("fallback para 'sa'", saida);
    }

    [Fact]
    public async Task Api_ComAdministradorIgualAoRuntime_Recusa()
    {
        var saida = await RefusedAsync(Config(Admin("almirante_user_bd")));

        Assert.Contains("não pode ser a de runtime", saida);
    }

    [Theory]
    [InlineData("Warn")]
    [InlineData("Off")]
    public async Task Api_RecusaOModoPermissivoAntigoDeAuditoriaAdministrativa(string modo)
    {
        var env = Config(Admin("almirante_admin_bd"));
        env["Security__AdminPrivilegeCheck"] = modo;

        Assert.Contains("foi removida", await RefusedAsync(env));
    }
}

[Trait("Category", "RequiresSqlServer")]
public class ApiProcessSqlServerTests
{
    private static Dictionary<string, string> Config(SqlIdentityEnvironment env, string? adminCs = null)
    {
        var config = ApiProcess.BaseEnvironment();
        config["ConnectionStrings__almirante"] = env.AppCs;
        config["ConnectionStrings__AlmiranteAdmin"] = adminCs ?? env.AdminCs;
        config["DbCredentials__AppUser"] = env.AppUser;
        return config;
    }

    // Sessões que o SQL Server viu vindas do processo da API: só podem ser das duas identidades da aplicação.
    private static async Task<List<string>> LoginsDoProcessoAsync(int pid)
    {
        await using var harness = await OpenHarnessAsync();
        return await ColumnAsync(harness, $"SELECT DISTINCT LOWER(login_name) FROM sys.dm_exec_sessions WHERE host_process_id = {pid} AND is_user_process = 1");
    }

    [Fact]
    public async Task Api_SobeComAdministradorDedicado_ExecutaOperacoesNormais_EReiniciaSemSa()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();

        for (var partida = 1; partida <= 2; partida++)
        {
            await using var api = ApiProcess.Start(Config(env));
            var (started, exitCode) = await api.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.True(started, $"partida {partida}: a API deveria subir (código {exitCode}). Saída:\n{api.Output}");

            using var client = api.CreateClient(await api.Listening);
            await ApiProcess.ExercitarOperacoesNormaisAsync(client);

            // O SQL Server só viu esta API autenticar como a identidade administrativa (migrations/provisionamento/
            // rotação) e como a de runtime — nunca como "sa" — e nenhum segredo apareceu na saída do processo.
            var logins = await LoginsDoProcessoAsync(api.ProcessId);
            Assert.Contains(env.AppUser.ToLowerInvariant(), logins);
            Assert.All(logins, login => Assert.Contains(login, new[] { env.AppUser.ToLowerInvariant(), env.AdminUser.ToLowerInvariant() }));
            Assert.DoesNotContain("sa", logins);
            NoSecret.Assert(api.Output, env.AdminPassword);
        }
    }

    [Fact]
    public async Task Api_ComAdministradorSysadmin_RecusaIniciar_SemAlterarOBanco()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        var login = $"sysadmin_{env.Suffix}";
        var senha = NewPassword();
        env.TrackLogin(login);
        await using (var harness = await OpenHarnessAsync())
        {
            await ExecAsync(harness, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{senha}', CHECK_POLICY = OFF; ALTER SERVER ROLE [sysadmin] ADD MEMBER [{login}];");
        }
        var adminCs = WithAdmin(new SqlConnectionStringBuilder(env.AppCs), login, senha);

        await using var api = ApiProcess.Start(Config(env, adminCs));
        var (started, exitCode) = await api.WaitAsync(TimeSpan.FromSeconds(120));

        Assert.False(started);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("papel de servidor sysadmin", api.Output);
        NoSecret.Assert(api.Output, senha);
        await using var verificacao = await OpenHarnessAsync(env.Database);
        Assert.Equal(0, await ScalarAsync(verificacao, "SELECT COUNT(*) FROM sys.tables"));
    }
}
