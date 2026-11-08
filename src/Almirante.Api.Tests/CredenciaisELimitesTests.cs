using System.Net;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

/// <summary>Rotação da credencial SQL sem banco (configuração e dicas de diagnóstico) e limites do login.</summary>
public sealed class CredenciaisELimitesTests
{
    [Theory]
    [InlineData(18456, "Confirme SQL_ADMIN_USER/SQL_ADMIN_PASSWORD")]
    [InlineData(4060, "Confirme SQL_ADMIN_USER/SQL_ADMIN_PASSWORD")]
    [InlineData(15151, "não tem a permissão prevista")]
    [InlineData(15247, "não tem a permissão prevista")]
    [InlineData(229, "não tem a permissão prevista")]
    [InlineData(262, "não tem a permissão prevista")]
    [InlineData(297, "não tem a permissão prevista")]
    [InlineData(51001, " Execute o bootstrap")]
    public void DicaDeDiagnostico_OrientaOOperadorPeloNumeroDoErro(int numero, string trecho) =>
        Assert.Contains(trecho, DbCredentialManager.DicaDeDiagnostico(numero));

    [Fact]
    public void DicaDeDiagnostico_ErroDesconhecido_NaoInventaOrientacao() => Assert.Equal("", DbCredentialManager.DicaDeDiagnostico(1205));

    [Fact]
    public async Task Rotacao_SemConexaoAdministrativa_FalhaComMensagemClara()
    {
        var manager = new DbCredentialManager(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:almirante"] = "Server=127.0.0.1,1;Database=x",
            }).Build(),
            Microsoft.Extensions.Options.Options.Create(new DbCredentialOptions { AppUser = "almirante_user_bd" }), new AppDbCredentialProvider(),
            NullLogger<DbCredentialManager>.Instance);

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RotateAsync());
        Assert.Equal("ConnectionStrings:AlmiranteAdmin é obrigatória quando DbCredentials:AppUser está definido.", erro.Message);
    }

    [Theory]
    [InlineData("LoginProtection:RateLimit:PermitLimit", "0")]
    [InlineData("LoginProtection:RateLimit:PermitLimit", "10001")]
    [InlineData("LoginProtection:RateLimit:WindowSeconds", "0")]
    [InlineData("LoginProtection:RateLimit:WindowSeconds", "3601")]
    public void LimiteDoLoginForaDaFaixa_ImpedeOStartup(string chave, string valor)
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { [chave] = valor });
        var erro = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("LoginProtection:RateLimit inválido (PermitLimit 1-10000, WindowSeconds 1-3600).", Achatar(erro));
    }

    [Theory]
    [InlineData("LoginProtection:Lockout:MaxFailedAttempts", "0")]
    [InlineData("LoginProtection:Lockout:MaxFailedAttempts", "1001")]
    [InlineData("LoginProtection:Lockout:FailureWindowMinutes", "0")]
    [InlineData("LoginProtection:Lockout:FailureWindowMinutes", "1441")]
    [InlineData("LoginProtection:Lockout:LockoutMinutes", "0")]
    [InlineData("LoginProtection:Lockout:LockoutMinutes", "1441")]
    public void BloqueioDeContaForaDaFaixa_ImpedeOStartup(string chave, string valor)
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { [chave] = valor });
        var erro = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("LoginProtection:Lockout inválido", Achatar(erro));
    }

    [Fact]
    public async Task LimiteDoLogin_TrataIpv4MapeadoEmIpv6ComoOMesmoCliente()
    {
        await using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["LoginProtection:RateLimit:PermitLimit"] = "1" });
        var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(client, "ninguem@local.dev", "senha errada", remoteIp: "198.51.100.7")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await TestHelpers.PostLoginAsync(client, "ninguem@local.dev", "senha errada", remoteIp: "::ffff:198.51.100.7")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(client, "ninguem@local.dev", "senha errada", remoteIp: "198.51.100.8")).StatusCode);
    }

    private static string Achatar(Exception exception)
    {
        var texto = new System.Text.StringBuilder();
        for (Exception? atual = exception; atual is not null; atual = atual.InnerException) texto.AppendLine(atual.Message);
        return texto.ToString();
    }
}

/// <summary>Verificação de menor privilégio depois da rotação, contra SQL Server com as identidades do bootstrap.</summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class DbCredentialManagerSqlServerTests
{
    [Fact]
    public async Task Rotacao_IdentidadeDeRuntimeForaDaRole_EhRecusadaComOAchado()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        var manager = env.NewManager();
        await manager.InitializeAsync();
        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"ALTER ROLE [{DbCredentialManager.AppRoleName}] DROP MEMBER [{env.AppUser}];");
        }

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RotateAsync());
        Assert.Contains($"não é membro de {DbCredentialManager.AppRoleName}", erro.Message);
        NoSecret.Assert(erro.Message, env.CurrentAppPassword(), "mensagem de erro");
    }

    [Fact]
    public async Task Rotacao_ComConexaoDeRuntimeQueNaoAbre_FalhaSemExporASenha()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        await env.NewManager().InitializeAsync();
        var foraDoBanco = new SqlConnectionStringBuilder(env.AppCs) { InitialCatalog = $"inexistente_{Guid.NewGuid():N}" }.ConnectionString;
        var manager = env.NewManager(extra: new Dictionary<string, string?> { ["ConnectionStrings:almirante"] = foraDoBanco });

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RotateAsync());
        Assert.StartsWith("Falha ao abrir uma conexão com a credencial de runtime recém-rotacionada (erro SQL", erro.Message);
        NoSecret.Assert(erro.Message, env.CurrentAppPassword(), "mensagem de erro");
    }

    [Fact]
    public async Task Rotacao_SemConexaoDeRuntime_FalhaDepoisDeTrocarASenhaComMensagemClara()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        await env.NewManager().InitializeAsync();
        var manager = env.NewManager(extra: new Dictionary<string, string?> { ["ConnectionStrings:almirante"] = null });

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RotateAsync());
        Assert.Equal("ConnectionStrings:almirante é obrigatória.", erro.Message);
    }
}
