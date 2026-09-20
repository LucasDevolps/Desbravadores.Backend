using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Almirante.Api.Cli;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Almirante.Api.Tests;

// Regressões dos bloqueadores da validação da PR #59 (relatório de validação da PR #59): cada teste
// falhava antes da correção correspondente.
public sealed class SecurityRegressionTests
{
    // Suspende o SaveChanges do login (quando há uma AuthSession sendo inserida) até o teste liberar,
    // para reproduzir de forma determinística um reset de senha concluindo no meio do login.
    private sealed class LoginGateInterceptor : SaveChangesInterceptor
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed = 1;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuthSession>().Any(e => e.State == EntityState.Added)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Reached.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    // No SQL Server o SaveChanges é uma transação: quando o UPDATE de Usuarios (token de concorrência) falha,
    // a sessão e o refresh do mesmo login são revertidos. (O provider InMemory não é transacional.)
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task ResetConcluidoNoMeioDoLogin_NaoDeixaSessaoUtilizavel()
    {
        var gate = new LoginGateInterceptor();
        await using var factory = new SqlServerApiFactory(configureDb: o => o.AddInterceptors(gate));
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "CorridaLoginReset", "DS");

        var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var login = TestHelpers.PostLoginAsync(client, usuario.Email, TestHelpers.SenhaPadraoTeste);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Login já verificou a senha antiga e está prestes a persistir a sessão; o reset conclui agora.
        using (var scope = factory.Services.CreateScope())
        {
            var resultado = await AdminPasswordResetCli.ResetPasswordAsync(
                scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>(),
                scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                usuario.Email, "Uma$enhaNovaBemForte2026");
            Assert.Equal(AdminPasswordResetCli.ResetResult.Sucesso, resultado.Resultado);
        }

        gate.Release.SetResult();
        Assert.Equal(HttpStatusCode.Unauthorized, (await login).StatusCode);

        var sessoesUtilizaveis = await TestHelpers.WithDbAsync(factory, db => db.AuthSessions
            .CountAsync(s => s.UsuarioId == usuario.Id && s.RevokedAtUtc == null));
        Assert.Equal(0, sessoesUtilizaveis);
        Assert.Equal(1, await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Id == usuario.Id).Select(u => u.SecurityVersion).SingleAsync()));

        // A senha nova continua funcionando (o reset não foi desfeito por rehash/gravação antiga).
        var novo = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novo);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(novo, usuario.Email, "Uma$enhaNovaBemForte2026")).StatusCode);
    }

    [Fact]
    public async Task Reset_IncrementaSecurityVersion_InvalidandoAccessTokenAnterior()
    {
        await using var factory = new ConfiguredApiFactory("Production");
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "InvalidaAccessToken", "DS");
        var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        var resp = await TestHelpers.PostLoginAsync(client, usuario.Email, TestHelpers.SenhaPadraoTeste);
        var token = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Almirante.Api.Dtos.LoginResponse>(resp.Content))!.Token.AccessToken;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Auth/Me")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            await AdminPasswordResetCli.ResetPasswordAsync(
                scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>(),
                scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                usuario.Email, "Outra$enhaBemForte2026x");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/Auth/Me")).StatusCode);
    }

    // Um Development publicado (IIS/Pop!_OS) liga a mesma política do Production por configuração.
    [Theory]
    [InlineData("Development", null, false)]
    [InlineData("Development", "false", false)]
    [InlineData("Development", "true", true)]
    [InlineData("Production", null, true)]
    public void PoliticaTls_AplicaEmProductionEEmDevelopmentPublicado(string environment, string? flag, bool esperado)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            flag is null ? [] : new Dictionary<string, string?> { [SqlServerConnectionSecurityValidator.EnforceSettingName] = flag }).Build();
        var env = new FakeEnvironment(environment);

        Assert.Equal(esperado, SqlServerConnectionSecurityValidator.ShouldEnforce(env, configuration));
    }

    [Fact]
    public void PoliticaTls_DevelopmentPublicado_RejeitaConexaoInsegura()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SqlServerConnectionSecurityValidator.EnforceSettingName] = "true",
        }).Build();
        var validator = new SqlServerConnectionSecurityValidator(new FakeEnvironment("Development"), configuration);

        var insegura = validator.Validate(null, new Almirante.Api.Options.ConnectionStringsOptions
        {
            Almirante = "Server=x;Database=d;Encrypt=True;TrustServerCertificate=True",
            AlmiranteAdmin = "Server=x;Database=d;Encrypt=False",
        });
        Assert.True(insegura.Failed);
        Assert.Equal(2, insegura.Failures!.Count());

        var segura = validator.Validate(null, new Almirante.Api.Options.ConnectionStringsOptions
        {
            Almirante = "Server=x;Database=d;Encrypt=True;TrustServerCertificate=False",
        });
        Assert.True(segura.Succeeded);
    }

    // Reprodução do bloqueador 1: a CLI (mesmo binário, mesma configuração de Production) precisa ser
    // recusada pela validação ANTES de qualquer conexão — o servidor abaixo nem existe, então, se a CLI
    // chegasse a conectar, a saída seria outro erro (falha de conexão) e não a recusa por TLS.
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task Cli_RecusaConexaoInsegura_AntesDeConectar(string environment, bool flagDevelopmentPublicado)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Almirante.Api.dll");
        Assert.True(File.Exists(dll), "Almirante.Api.dll deve estar no diretório de saída dos testes.");

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {AdminPasswordResetCli.CommandName} admin@local.dev")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        psi.Environment["ConnectionStrings__almirante"] = "Server=127.0.0.1,1;Database=x;Encrypt=True;TrustServerCertificate=True";
        psi.Environment["ConnectionStrings__AlmiranteAdmin"] = "Server=127.0.0.1,1;Database=x;User Id=sa;Password=nao-usada;Encrypt=True;TrustServerCertificate=True";
        psi.Environment["DbCredentials__AppUser"] = "almirante_user_bd";
        psi.Environment["Jwt__Issuer"] = "t";
        psi.Environment["Jwt__Audience"] = "t";
        psi.Environment["Jwt__ActiveKeyId"] = "v1";
        psi.Environment["Jwt__Keys__v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (flagDevelopmentPublicado) psi.Environment["Security__RequireTrustedSqlServerCertificate"] = "true";

        using var process = Process.Start(psi)!;
        await process.StandardInput.WriteLineAsync("Uma$enhaNovaBemForte2026");
        await process.StandardInput.WriteLineAsync("Uma$enhaNovaBemForte2026");
        process.StandardInput.Close();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("TrustServerCertificate=True", stderr);
        Assert.DoesNotContain("nao-usada", stderr);
    }


    private static async Task<HttpResponseMessage> GetCsrfAsync(HttpClient client, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Auth/csrf");
        request.Headers.Host = host;
        return await client.SendAsync(request);
    }

    // Development publicado (HTTPS real) emite HSTS quando habilitado; sem a flag, nunca (comportamento de sempre).
    [Theory]
    [InlineData("true", true)]
    [InlineData(null, false)]
    public async Task Hsts_EmDevelopmentPublicado_DependeDaConfiguracao_NaoSoDoAmbiente(string? flag, bool esperado)
    {
        var overrides = new Dictionary<string, string?>();
        if (flag is not null) overrides["Hsts:EnabledInDevelopment"] = flag;
        await using var factory = new ConfiguredApiFactory("Development", overrides);
        var response = await GetCsrfAsync(factory.CreateClient(), "api.exemplo.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(esperado, response.Headers.Contains("Strict-Transport-Security"));
    }

    // Host desconhecido não chega ao pipeline (nem a redirects que o refletiriam): HostFiltering => 400.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AllowedHosts_RejeitaHostDesconhecido(string environment)
    {
        await using var factory = new ConfiguredApiFactory(environment, new Dictionary<string, string?> { ["AllowedHosts"] = "api.exemplo.com" });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetCsrfAsync(client, "api.exemplo.com")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetCsrfAsync(client, "attacker.example")).StatusCode);
    }


    // Transição "Despesa" -> "Saida": o nome legado continua aceito na entrada; a resposta usa "Saida" e
    // só volta a "Despesa" durante a janela de compatibilidade (Compatibility:LegacyTipoFluxoDespesa).
    [Theory]
    [InlineData("\"Despesa\"", 1)]
    [InlineData("\"despesa\"", 1)]
    [InlineData("\"Saida\"", 1)]
    [InlineData("\"Saída\"", 1)]
    [InlineData("1", 1)]
    [InlineData("\"Entrada\"", 0)]
    public void TipoFluxo_AceitaNomeLegadoNaEntrada(string json, int esperado)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new Almirante.Api.Entities.TipoFluxoLancamentoJsonConverter(false) } };
        Assert.Equal((Almirante.Api.Entities.TipoFluxoLancamento)esperado,
            System.Text.Json.JsonSerializer.Deserialize<Almirante.Api.Entities.TipoFluxoLancamento>(json, options));
    }

    [Theory]
    [InlineData("\"Outro\"")]
    [InlineData("7")]
    [InlineData("null")]
    public void TipoFluxo_RecusaValorDesconhecido(string json)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new Almirante.Api.Entities.TipoFluxoLancamentoJsonConverter(false) } };
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<Almirante.Api.Entities.TipoFluxoLancamento>(json, options));
    }

    [Theory]
    [InlineData(false, "\"Saida\"")]
    [InlineData(true, "\"Despesa\"")]
    public void TipoFluxo_RespostaUsaSaida_SalvoJanelaDeCompatibilidade(bool legado, string esperado)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new Almirante.Api.Entities.TipoFluxoLancamentoJsonConverter(legado) } };
        Assert.Equal(esperado, System.Text.Json.JsonSerializer.Serialize(Almirante.Api.Entities.TipoFluxoLancamento.Saida, options));
        Assert.Equal("\"Entrada\"", System.Text.Json.JsonSerializer.Serialize(Almirante.Api.Entities.TipoFluxoLancamento.Entrada, options));
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
