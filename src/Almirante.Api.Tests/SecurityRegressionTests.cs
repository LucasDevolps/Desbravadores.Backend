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

public sealed class SecurityRegressionTests
{
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
        psi.Environment["ConnectionStrings__AlmiranteAdmin"] = "Server=127.0.0.1,1;Database=x;User Id=almirante_admin_bd;Password=nao-usada;Encrypt=True;TrustServerCertificate=True";
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

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/8")]
    [InlineData("::/0")]
    public async Task TrustedNetworkCidr_AmploDemais_RecusaIniciar(string cidr)
    {
        await using var factory = new ConfiguredApiFactory("Production",
            new Dictionary<string, string?> { ["ReverseProxy:TrustedNetworkCidr"] = cidr });

        var excecao = await Assert.ThrowsAnyAsync<Exception>(async () => await GetCsrfAsync(factory.CreateClient(), "api.exemplo.com"));
        Assert.Contains("TrustedNetworkCidr", Achatar(excecao), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("172.30.0.0/24")]
    [InlineData("127.0.0.1/32")]
    [InlineData("fd00::/64")]
    public async Task TrustedNetworkCidr_RestritoOSuficiente_Inicia(string cidr)
    {
        await using var factory = new ConfiguredApiFactory("Production",
            new Dictionary<string, string?> { ["ReverseProxy:TrustedNetworkCidr"] = cidr, ["AllowedHosts"] = "*" });

        Assert.Equal(HttpStatusCode.OK, (await GetCsrfAsync(factory.CreateClient(), "api.exemplo.com")).StatusCode);
    }

    [Theory]
    [InlineData("Development", null, true)]
    [InlineData("Development", "false", false)]
    [InlineData("Production", null, false)]
    [InlineData("Development", "", true)]
    [InlineData("Production", "", false)]
    [InlineData("Production", "   ", false)]
    [InlineData("Production", "true", true)]
    public async Task Swagger_SoEhServidoQuandoHabilitado(string environment, string? flag, bool esperado)
    {
        var overrides = new Dictionary<string, string?> { ["AllowedHosts"] = "*" };
        if (flag is not null) overrides["Swagger:Enabled"] = flag;
        await using var factory = new ConfiguredApiFactory(environment, overrides);

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(esperado, response.StatusCode == HttpStatusCode.OK);
    }

    private static string Achatar(Exception exception)
    {
        var texto = new System.Text.StringBuilder();
        for (Exception? atual = exception; atual is not null; atual = atual.InnerException) texto.AppendLine(atual.Message);
        return texto.ToString();
    }


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
