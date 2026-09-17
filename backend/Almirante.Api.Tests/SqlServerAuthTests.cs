using System.Net;
using System.Reflection;
using Almirante.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

// Cenários que dependem de propriedades reais do SQL Server (rowversion, transações, índices e
// concorrência entre instâncias). Excluídos do CI padrão pela categoria; para rodar:
//   dotnet test backend/Almirante.Api.Tests --filter "Category=RequiresDocker"
// (com Docker disponível, ou com ALMIRANTE_TESTS_SQLSERVER apontando para um SQL Server local).
[Collection(SqlServerCollection.Name)]
[Trait("Category", "RequiresDocker")]
public class SqlServerAuthTests(SqlServerAuthFixture fixture)
{
    private const int Iteracoes = 20;

    [Fact]
    public async Task BancoNovo_AplicaTodasAsMigrations()
    {
        var esperadas = typeof(AlmiranteDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id).OfType<string>().Order().ToArray();

        var aplicadas = await fixture.QueryAsync(db => db.Database.GetAppliedMigrationsAsync());

        Assert.Equal(esperadas, aplicadas.Order().ToArray());
    }

    [Fact]
    public async Task RefreshSimultaneo_EmDuasInstancias_NuncaEntregaDoisSucessores()
    {
        using var a = fixture.CreateClient(fixture.A);
        using var b = fixture.CreateClient(fixture.B);

        for (var i = 0; i < Iteracoes; i++)
        {
            var jar = new CookieJar();
            var token = await AuthFlow.LoginAsync(a, jar, SqlServerAuthFixture.AdminEmail, fixture.AdminSenha);
            var csrf = await AuthFlow.GetCsrfAsync(a, jar);
            var sid = AuthFlow.SessionId(token);

            var respostas = await Task.WhenAll(
                AuthFlow.RefreshAsync(a, jar.Clone(), csrf),
                AuthFlow.RefreshAsync(b, jar.Clone(), csrf));

            Assert.DoesNotContain(respostas, r => (int)r.StatusCode >= 500);
            Assert.True(respostas.Count(r => r.StatusCode == HttpStatusCode.OK) <= 1, "duas renovações aceitas para o mesmo refresh");
            var (ativa, vigentes) = await fixture.QueryAsync(async db =>
            {
                var sessao = await db.AuthSessions.AsNoTracking().SingleAsync(s => s.Id == sid);
                var tokens = await db.RefreshTokens.CountAsync(t => t.SessionId == sid && t.ConsumedAtUtc == null && t.RevokedAtUtc == null);
                return (sessao.RevokedAtUtc is null, tokens);
            });
            Assert.False(ativa && vigentes > 1, "cadeia bifurcada com sessão ativa");
        }
    }

    [Fact]
    public async Task LogoutConcorrenteComRefresh_SempreRevogaSemErro()
    {
        using var a = fixture.CreateClient(fixture.A);
        using var b = fixture.CreateClient(fixture.B);

        for (var i = 0; i < Iteracoes; i++)
        {
            var jar = new CookieJar();
            var token = await AuthFlow.LoginAsync(a, jar, SqlServerAuthFixture.AdminEmail, fixture.AdminSenha);
            var csrf = await AuthFlow.GetCsrfAsync(a, jar);
            var sid = AuthFlow.SessionId(token);

            var refreshTask = AuthFlow.RefreshAsync(a, jar.Clone(), csrf);
            var logoutTask = AuthFlow.LogoutAsync(b, jar.Clone(), csrf);
            await Task.WhenAll(refreshTask, logoutTask);
            var refresh = await refreshTask;
            var logout = await logoutTask;

            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.NotEqual(HttpStatusCode.InternalServerError, refresh.StatusCode);
            var revogada = await fixture.QueryAsync(db => db.AuthSessions.AsNoTracking().AnyAsync(s => s.Id == sid && s.RevokedAtUtc != null));
            Assert.True(revogada, $"sessão continuou ativa após logout 204 (iteração {i})");
            if (refresh.StatusCode == HttpStatusCode.OK)
            {
                var novoToken = await AuthFlow.ReadAccessTokenAsync(refresh);
                Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlow.MeAsync(a, novoToken)).StatusCode);
            }
        }
    }

    [Fact]
    public async Task LogoutsSimultaneos_DaMesmaSessao_SaoIdempotentes()
    {
        using var a = fixture.CreateClient(fixture.A);
        using var b = fixture.CreateClient(fixture.B);

        for (var i = 0; i < Iteracoes; i++)
        {
            var jar = new CookieJar();
            await AuthFlow.LoginAsync(a, jar, SqlServerAuthFixture.AdminEmail, fixture.AdminSenha);
            var csrf = await AuthFlow.GetCsrfAsync(a, jar);

            var respostas = await Task.WhenAll(AuthFlow.LogoutAsync(a, jar.Clone(), csrf), AuthFlow.LogoutAsync(b, jar.Clone(), csrf));

            Assert.All(respostas, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        }
    }

    [Fact]
    public async Task ReusoDeRefresh_RevogaAFamilia_EAOutraInstanciaRecusaOsTokens()
    {
        using var a = fixture.CreateClient(fixture.A);
        using var b = fixture.CreateClient(fixture.B);
        var jar = new CookieJar();
        var token = await AuthFlow.LoginAsync(a, jar, SqlServerAuthFixture.AdminEmail, fixture.AdminSenha);
        var csrf = await AuthFlow.GetCsrfAsync(a, jar);
        var sid = AuthFlow.SessionId(token);
        var consumido = jar.Clone();

        var rotacao = await AuthFlow.RefreshAsync(a, jar, csrf);
        var sucessor = await AuthFlow.ReadAccessTokenAsync(rotacao);
        var replay = await AuthFlow.RefreshAsync(b, consumido, csrf);

        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var motivo = await fixture.QueryAsync(db => db.AuthSessions.AsNoTracking().Where(s => s.Id == sid).Select(s => s.RevocationReason).SingleAsync());
        Assert.Equal("refresh-token-reuse", motivo);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlow.RefreshAsync(b, jar, csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlow.MeAsync(b, sucessor)).StatusCode);
    }

    [Fact]
    public async Task Logout_EmUmaInstancia_InvalidaOAccessTokenNaOutra()
    {
        using var a = fixture.CreateClient(fixture.A);
        using var b = fixture.CreateClient(fixture.B);
        var jar = new CookieJar();
        var token = await AuthFlow.LoginAsync(a, jar, SqlServerAuthFixture.AdminEmail, fixture.AdminSenha);
        Assert.Equal(HttpStatusCode.OK, (await AuthFlow.MeAsync(b, token)).StatusCode);

        var csrf = await AuthFlow.GetCsrfAsync(b, jar);
        Assert.Equal(HttpStatusCode.NoContent, (await AuthFlow.LogoutAsync(b, jar, csrf)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlow.MeAsync(a, token)).StatusCode);
    }
}
