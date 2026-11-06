using System.Diagnostics;
using Almirante.Api.Cli;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Identity;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

/// <summary>
/// <c>reset-admin-password</c> no processo real, contra SQL Server com as identidades do bootstrap: a CLI audita a identidade
/// administrativa, troca a senha pela conexão administrativa (sem subir o servidor HTTP) e revoga as sessões, sem ecoar a senha.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AdminPasswordResetCliProcessTests
{
    private static Dictionary<string, string> Config(SqlIdentityEnvironment env)
    {
        var config = ApiProcess.BaseEnvironment();
        config["ConnectionStrings__almirante"] = env.AppCs;
        config["ConnectionStrings__AlmiranteAdmin"] = env.AdminCs;
        config["DbCredentials__AppUser"] = env.AppUser;
        return config;
    }

    private static async Task<(int ExitCode, string Saida, string Erro)> ExecutarCliAsync(IDictionary<string, string> ambiente, string email, params string[] linhas)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Almirante.Api.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add(AdminPasswordResetCli.CommandName);
        psi.ArgumentList.Add(email);
        foreach (var (chave, valor) in ambiente) psi.Environment[chave] = valor;

        using var processo = Process.Start(psi)!;
        foreach (var linha in linhas) await processo.StandardInput.WriteLineAsync(linha);
        processo.StandardInput.Close();
        var saida = processo.StandardOutput.ReadToEndAsync();
        var erro = processo.StandardError.ReadToEndAsync();
        await processo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        return (processo.ExitCode, await saida, await erro);
    }

    [Fact]
    public async Task ResetAdminPassword_TrocaASenhaPelaIdentidadeAdministrativa_ERevogaSessoes()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        var ambiente = Config(env);
        await using (var api = ApiProcess.Start(ambiente))
        {
            var (subiu, codigo) = await api.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.True(subiu, $"a API deveria subir para migrar e semear (código {codigo}). Saída:\n{api.Output}");
            using var client = api.CreateClient(await api.Listening);
            await ApiProcess.ExercitarOperacoesNormaisAsync(client);
        }

        string versaoAntes;
        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            versaoAntes = (await ColumnAsync(harness, $"SELECT CAST(SecurityVersion AS varchar(20)) FROM dbo.Usuarios WHERE EmailNormalizado = '{ApiProcess.AdminEmail.ToUpperInvariant()}'")).Single();
            await ExecAsync(harness, """
                INSERT INTO dbo.AuthSessions (Id, UsuarioId, CreatedAtUtc, LastRenewedAtUtc, AbsoluteExpiresAtUtc, SecurityVersion)
                SELECT NEWID(), Id, SYSUTCDATETIME(), SYSUTCDATETIME(), DATEADD(day, 1, SYSUTCDATETIME()), SecurityVersion FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV';
                """);
        }

        const string novaSenha = "Frase de emergencia do operador 2026";
        var (exitCode, saida, erro) = await ExecutarCliAsync(ambiente, ApiProcess.AdminEmail, novaSenha, novaSenha);

        Assert.True(exitCode == 0, $"código {exitCode}. stdout:\n{saida}\nstderr:\n{erro}");
        Assert.Contains("Senha atualizada e sessões ativas dessa conta foram revogadas", saida);
        Assert.DoesNotContain(novaSenha, saida + erro);
        NoSecret.Assert(saida + erro, env.AdminPassword);

        await using var verificacao = await OpenHarnessAsync(env.Database);
        Assert.Equal(0, await ScalarAsync(verificacao,
            "SELECT COUNT(*) FROM dbo.AuthSessions s JOIN dbo.Usuarios u ON u.Id = s.UsuarioId WHERE u.EmailNormalizado = 'ADMIN@LOCAL.DEV' AND s.RevokedAtUtc IS NULL"));
        var gravado = (await ColumnAsync(verificacao,
            "SELECT CONCAT(CAST(SecurityVersion AS varchar(20)), '|', SenhaHash) FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV'")).Single().Split('|', 2);
        Assert.Equal(long.Parse(versaoAntes) + 1, long.Parse(gravado[0]));
        var hasher = new PasswordHasher<Usuario>();
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(null!, gravado[1], novaSenha));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(null!, gravado[1], ApiProcess.AdminSenha));

        var (codigoInexistente, _, erroInexistente) = await ExecutarCliAsync(ambiente, "ninguem@local.dev", novaSenha, novaSenha);
        Assert.Equal(1, codigoInexistente);
        Assert.Contains("Nenhum usuário encontrado com esse e-mail.", erroInexistente);

        // Sem DbCredentials (como no IIS com autenticação do Windows), a CLI usa a própria conexão da aplicação.
        var semCredenciais = ApiProcess.BaseEnvironment();
        semCredenciais["ConnectionStrings__almirante"] = env.AdminCs;
        const string outraSenha = "Outra frase de emergencia do operador 2026";
        var (codigoSemCredenciais, saidaSemCredenciais, erroSemCredenciais) =
            await ExecutarCliAsync(semCredenciais, ApiProcess.AdminEmail, outraSenha, outraSenha);
        Assert.True(codigoSemCredenciais == 0, $"código {codigoSemCredenciais}. stdout:\n{saidaSemCredenciais}\nstderr:\n{erroSemCredenciais}");
        NoSecret.Assert(saidaSemCredenciais + erroSemCredenciais, env.AdminPassword);
        var regravado = (await ColumnAsync(verificacao, "SELECT SenhaHash FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV'")).Single();
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(null!, regravado, outraSenha));
    }
}
