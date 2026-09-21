using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Almirante.Api.Tests;

// Política de identidades SQL da API, sem SQL Server: "sa" e o segredo dele nunca chegam ao processo, não
// existe fallback para "sa" e a identidade administrativa é dedicada e obrigatória.
public class SqlIdentityPolicyTests
{
    private const string Runtime = "Server=sqlserver,1433;Database=almirante;Encrypt=True;TrustServerCertificate=False";
    private const string SenhaAdmin = "Senha-Admin-Unica-9x7q2Zk";

    private static string Admin(string user = "almirante_admin_bd", string? password = SenhaAdmin, string database = "almirante") =>
        $"Server=sqlserver,1433;Database={database};User Id={user};" + (password is null ? "" : $"Password={password};") + "Encrypt=True;TrustServerCertificate=False";

    private static List<string> Falhas(string? runtime, string? admin, string? appUser, IDictionary<string, string?>? extra = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(extra ?? new Dictionary<string, string?>()).Build();
        return SqlIdentityPolicy.Validate(runtime, admin, appUser, configuration).ToList();
    }

    [Fact]
    public void ConfiguracaoCorreta_NaoTemFalhas() =>
        Assert.Empty(Falhas(Runtime, Admin(), "almirante_user_bd"));

    [Fact]
    public void SemDbCredentials_SemSa_NaoTemFalhas_ComoNoIisComAutenticacaoDoWindows() =>
        Assert.Empty(Falhas("Server=srv;Database=almirante;Trusted_Connection=True", null, null));

    [Theory]
    [InlineData("sa")]
    [InlineData("SA")]
    [InlineData("Sa")]
    [InlineData(" sa ")]
    [InlineData("[sa]")]
    public void Sa_ComoIdentidadeAdministrativa_ERecusado_SemVazarASenha(string user)
    {
        var falhas = Falhas(Runtime, Admin(user), "almirante_user_bd");

        Assert.Contains(falhas, f => f.Contains("'sa'") && f.Contains(DbCredentialManager.AdminConnectionName));
        Assert.DoesNotContain(falhas, f => f.Contains(SenhaAdmin));
    }

    [Fact]
    public void Sa_NaConnectionStringDeRuntime_ERecusado_MesmoSemDbCredentials() =>
        Assert.Contains(Falhas("Server=x;Database=almirante;User Id=sa;Password=y", null, null), f => f.Contains("'sa'"));

    [Fact]
    public void Sa_ComoUsuarioDeRuntime_ERecusado() =>
        Assert.Contains(Falhas(Runtime, Admin(), "sa"), f => f.Contains("AppUser") && f.Contains("'sa'"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NaoExisteFallbackParaSa_IdentidadeAdministrativaAusenteFalha(string? admin)
    {
        var falhas = Falhas(Runtime, admin, "almirante_user_bd");

        Assert.Contains(falhas, f => f.Contains("obrigatória") && f.Contains("fallback para 'sa'"));
    }

    [Fact]
    public void IdentidadeAdministrativa_SemSenha_Falha() =>
        Assert.Contains(Falhas(Runtime, Admin(password: null), "almirante_user_bd"), f => f.Contains("precisa de User Id e Password"));

    [Fact]
    public void IdentidadeAdministrativa_IgualAdeRuntime_Falha() =>
        Assert.Contains(Falhas(Runtime, Admin("Almirante_User_BD"), "almirante_user_bd"), f => f.Contains("não pode ser a de runtime"));

    [Fact]
    public void ConexoesParaBancosDiferentes_Falham() =>
        Assert.Contains(Falhas(Runtime, Admin(database: "outro"), "almirante_user_bd"), f => f.Contains("mesmo banco"));

    [Fact]
    public void RuntimeComCredencial_OuSemBanco_Falha()
    {
        Assert.Contains(Falhas("Server=x;Database=almirante;User Id=u;Password=p", Admin(), "almirante_user_bd"),
            f => f.Contains("não pode conter User Id/Password"));
        Assert.Contains(Falhas("Server=x", Admin(), "almirante_user_bd"), f => f.Contains("Database=<banco>"));
    }

    [Fact]
    public void AutenticacaoIntegradaComoAdministrador_EAceita_AAuditoriaDePrivilegioContinuaObrigatoria() =>
        Assert.Empty(Falhas(Runtime, "Server=sqlserver;Database=almirante;Integrated Security=True", "almirante_user_bd"));

    [Theory]
    [InlineData("SQL_SA_PASSWORD")]
    [InlineData("MSSQL_SA_PASSWORD")]
    public void SegredoDoSa_NoAmbienteDaApi_Falha_SemVazarOValor(string nome)
    {
        const string valor = "ValorSecretoDoSa-123";
        var falhas = Falhas(Runtime, Admin(), "almirante_user_bd", new Dictionary<string, string?> { [nome] = valor });

        Assert.Contains(falhas, f => f.Contains(nome));
        Assert.DoesNotContain(falhas, f => f.Contains(valor));
    }

    [Theory]
    [InlineData("Warn")]
    [InlineData("Off")]
    [InlineData("qualquer")]
    public void ModoWarnOuOffDaAuditoriaAdministrativa_FoiRemovido_ENaoPodeVoltarEmSilencio(string modo) =>
        Assert.Contains(Falhas(Runtime, Admin(), "almirante_user_bd", new Dictionary<string, string?> { [SqlIdentityPolicy.AdminPrivilegeCheckSettingName] = modo }),
            f => f.Contains("foi removida"));

    [Fact]
    public void ModoEnforceLegado_EToleradoPoisNaoAfrouxaNada() =>
        Assert.Empty(Falhas(Runtime, Admin(), "almirante_user_bd", new Dictionary<string, string?> { [SqlIdentityPolicy.AdminPrivilegeCheckSettingName] = "Enforce" }));

    [Fact]
    public void Validador_FalhaOStartupPeloIStartupValidator()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = Runtime,
            ["ConnectionStrings:AlmiranteAdmin"] = Admin("sa"),
            ["DbCredentials:AppUser"] = "almirante_user_bd",
        }).Build();

        var resultado = new SqlIdentityPolicyValidator(configuration).Validate(null, new ConnectionStringsOptions());

        Assert.True(resultado.Failed);
        Assert.DoesNotContain(resultado.Failures!, f => f.Contains(SenhaAdmin));
    }

    [Fact]
    public void EnsureValid_LancaSemValores()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = Runtime,
            ["ConnectionStrings:AlmiranteAdmin"] = Admin("sa"),
            ["DbCredentials:AppUser"] = "almirante_user_bd",
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => SqlIdentityPolicy.EnsureValid(configuration));
        Assert.DoesNotContain(SenhaAdmin, ex.Message);
        Assert.DoesNotContain("sqlserver,1433", ex.Message);
    }

    // O portão de rotação: enquanto o ALTER USER não terminou, nenhuma abertura NOVA parte da credencial antiga.
    [Fact]
    public async Task Interceptor_EsperaATrocaDeCredencialEmAndamento_EUsaANovaCredencial()
    {
        var provider = new AppDbCredentialProvider();
        provider.Set("almirante_user_bd", "SenhaAntiga1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var interceptor = new AppDbCredentialInterceptor(provider);
        using var connection = new SqlConnection("Server=x;Database=d");

        var gate = provider.BeginSwitch();
        var abertura = interceptor.ConnectionOpeningAsync(connection, null!, default).AsTask();
        await Task.Delay(300);
        Assert.False(abertura.IsCompleted, "a abertura deveria esperar a troca da credencial");

        var anterior = provider.Set("almirante_user_bd", "SenhaNova2bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.NotNull(anterior);
        gate.Dispose();
        await abertura.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("almirante_user_bd", connection.Credential.UserId);
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(connection.Credential.Password);
        try { Assert.StartsWith("SenhaNova2", Marshal.PtrToStringUni(pointer)); }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }

    [Fact]
    public async Task Interceptor_SemTrocaEmAndamento_NaoEspera()
    {
        var provider = new AppDbCredentialProvider();
        provider.Set("u", "SenhaQualquer1aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var connection = new SqlConnection("Server=x;Database=d");

        await new AppDbCredentialInterceptor(provider).ConnectionOpeningAsync(connection, null!, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("u", connection.Credential.UserId);
    }

    [Fact]
    public async Task Interceptor_SemCredencialProvisionada_FalhaEmVezDeConectarSemUsuario()
    {
        using var connection = new SqlConnection("Server=x;Database=d");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AppDbCredentialInterceptor(new AppDbCredentialProvider()).ConnectionOpeningAsync(connection, null!, default).AsTask());
    }
}

// Regressão de repositório: nenhum arquivo versionado pode reintroduzir "sa" ou o segredo dele no que a API
// recebe (Compose, configuração, código) nem uma senha real em .env.example.
public class SqlIdentityRepositoryPolicyTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "compose.yaml"))) directory = directory.Parent;
        Assert.True(directory is not null, "compose.yaml não encontrado a partir do diretório de testes.");
        return directory!.FullName;
    }

    private static readonly string[] ComposeFiles = ["compose.yaml", "compose.tls.yaml", "compose.https.yaml"];

    // Bloco de um serviço do Compose: da linha "  nome:" até o próximo serviço (indentação de 2 espaços) ou seção.
    private static string ServiceBlock(string compose, string service)
    {
        var lines = compose.Replace("\r\n", "\n").Split('\n');
        var collected = new List<string>();
        var inside = false;
        var inServices = false;
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^\S")) { inServices = line.StartsWith("services:", StringComparison.Ordinal); inside = false; continue; }
            if (!inServices) continue;
            var header = Regex.Match(line, @"^  ([A-Za-z0-9_-]+):\s*$");
            if (header.Success) { inside = header.Groups[1].Value == service; continue; }
            if (inside) collected.Add(line);
        }
        return string.Join("\n", collected);
    }

    [Fact]
    public void Compose_NaoTemFallbackParaSaNemModoPermissivoDeAuditoria()
    {
        var root = RepositoryRoot();
        foreach (var name in ComposeFiles)
        {
            var text = File.ReadAllText(Path.Combine(root, name));
            var semComentarios = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            Assert.DoesNotMatch(new Regex(@"SQL_ADMIN_(USER|PASSWORD):-", RegexOptions.IgnoreCase), semComentarios);
            Assert.DoesNotMatch(new Regex(@":-sa\b", RegexOptions.IgnoreCase), semComentarios);
            Assert.DoesNotMatch(new Regex(@"User Id=sa\b", RegexOptions.IgnoreCase), semComentarios);
            Assert.DoesNotContain("SQL_ADMIN_PRIVILEGE_CHECK", semComentarios);
            Assert.DoesNotContain("Security__AdminPrivilegeCheck", semComentarios);
        }
    }

    [Fact]
    public void Compose_SegredoDoSa_SoExisteNoSqlServerENoBootstrap_NuncaNaApi()
    {
        var root = RepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "compose.yaml"));

        foreach (var service in new[] { "api", "nginx" })
        {
            var block = ServiceBlock(compose, service);
            Assert.NotEmpty(block);
            var semComentarios = string.Join("\n", block.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            Assert.DoesNotContain("SQL_SA_PASSWORD", semComentarios);
            Assert.DoesNotContain("MSSQL_SA_PASSWORD", semComentarios);
            Assert.DoesNotContain("SQLCMDPASSWORD", semComentarios);
            Assert.DoesNotContain("env_file", semComentarios);
        }

        Assert.Contains("MSSQL_SA_PASSWORD", ServiceBlock(compose, "sqlserver"));
        var bootstrap = ServiceBlock(compose, "sql-bootstrap");
        Assert.Contains("SQLCMDPASSWORD", bootstrap);
        Assert.Contains("restart: \"no\"", bootstrap);

        // A API exige o bootstrap concluído, e a conexão administrativa usa SQL_ADMIN_* obrigatórios.
        var api = ServiceBlock(compose, "api");
        Assert.Contains("sql-bootstrap:", api);
        Assert.Contains("service_completed_successfully", api);
        Assert.Contains("${SQL_ADMIN_USER:?", api);
        Assert.Contains("${SQL_ADMIN_PASSWORD:?", api);
    }

    [Fact]
    public void Compose_HealthcheckDoSqlServer_NaoUsaSaNemSenhaPrivilegiada()
    {
        var sqlserver = ServiceBlock(File.ReadAllText(Path.Combine(RepositoryRoot(), "compose.yaml")), "sqlserver");
        var healthcheck = string.Join("\n", sqlserver.Split('\n').SkipWhile(l => !l.Contains("healthcheck:")).Where(l => !l.TrimStart().StartsWith('#')));

        Assert.NotEmpty(healthcheck);
        Assert.DoesNotContain("-U sa", healthcheck);
        Assert.DoesNotContain("MSSQL_SA_PASSWORD", healthcheck);
        Assert.DoesNotContain("SQLCMDPASSWORD", healthcheck);
    }

    [Fact]
    public void CodigoDaApi_NaoTemUsuarioSaNemSegredoDoSaForaDaPolitica()
    {
        var api = Path.Combine(RepositoryRoot(), "backend", "Almirante.Api");
        foreach (var file in Directory.EnumerateFiles(api, "*.*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".json") || f.EndsWith(".http") || f.EndsWith("Dockerfile")))
        {
            // Comentários explicam a política e podem citar os nomes; só o código conta.
            var text = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.False(Regex.IsMatch(text, @"User Id=sa\b|User ID=sa\b|-U sa\b", RegexOptions.IgnoreCase), $"{Path.GetFileName(file)} usa o login sa.");
            if (Path.GetFileName(file) != "SqlIdentityPolicy.cs")
                Assert.False(text.Contains("SQL_SA_PASSWORD") || text.Contains("MSSQL_SA_PASSWORD"), $"{Path.GetFileName(file)} referencia o segredo do sa.");
        }
    }

    // O AppHost (Aspire) também alimenta a API: WithReference(banco) injetaria uma connection string com "sa".
    [Fact]
    public void AppHost_NaoRepassaOSaParaAApi_ERodaOBootstrapAntesDela()
    {
        var text = string.Join('\n', File.ReadAllLines(Path.Combine(RepositoryRoot(), "backend", "Almirante.AppHost", "AppHost.cs"))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain(".WithReference(", text);
        Assert.DoesNotContain("AddDatabase(", text);
        Assert.Contains(".WaitForCompletion(bootstrap)", text);
        var api = text[text.IndexOf("AddProject<", StringComparison.Ordinal)..];
        Assert.DoesNotContain("saPassword", api);
        Assert.DoesNotContain("SQL_SA_PASSWORD", api);
        Assert.DoesNotContain("User Id=sa", api, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvExample_SoTemPlaceholdersParaSegredos()
    {
        var linhas = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".env.example"))
            .Where(l => !l.TrimStart().StartsWith('#') && l.Contains('='))
            .Select(l => l.Split('=', 2));

        foreach (var parts in linhas.Where(p => Regex.IsMatch(p[0], @"(PASSWORD|SENHA|SECRET|TOKEN)$|_KEY_V[0-9]+$", RegexOptions.IgnoreCase)))
            Assert.StartsWith("DEFINA_", parts[1].Trim());
    }
}
