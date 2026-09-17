using Almirante.Api.Data;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

[Collection(SqlServerCollection.Name)]
[Trait("Category", "RequiresDocker")]
public class SqlServerSessionCleanupTests(SqlServerAuthFixture fixture)
{
    private async Task<Guid> CriarSessaoComRotacoesAsync(HttpClient client, int rotacoes)
    {
        var jar = new CookieJar();
        var token = await AuthFlow.LoginAsync(client, jar, SqlServerAuthFixture.AdminEmail, fixture.AdminSenha);
        var csrf = await AuthFlow.GetCsrfAsync(client, jar);
        for (var i = 0; i < rotacoes; i++) (await AuthFlow.RefreshAsync(client, jar, csrf)).EnsureSuccessStatusCode();
        return AuthFlow.SessionId(token);
    }

    private Task VencerAsync(Guid sessionId, TimeSpan haQuantoTempo) => fixture.QueryAsync(async db =>
        await db.AuthSessions.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AbsoluteExpiresAtUtc, DateTime.UtcNow - haQuantoTempo)));

    private async Task<int> LimparAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> instancia, int batchSize)
    {
        using var scope = instancia.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthSessionCleanup>().RemoveExpiredAsync(CancellationToken.None, batchSize);
    }

    [Fact]
    public async Task Limpeza_RemoveFamiliasVencidasComCadeia_EmLotes_PreservandoAMargemEAsAtivas()
    {
        using var client = fixture.CreateClient(fixture.A);
        var vencidas = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var sid = await CriarSessaoComRotacoesAsync(client, rotacoes: 2);
            await VencerAsync(sid, TimeSpan.FromHours(2));
            vencidas.Add(sid);
        }
        var naMargem = await CriarSessaoComRotacoesAsync(client, rotacoes: 1);
        await VencerAsync(naMargem, TimeSpan.FromMinutes(30));
        var ativa = await CriarSessaoComRotacoesAsync(client, rotacoes: 1);

        // Duas réplicas limpando ao mesmo tempo, com lotes pequenos para exercitar a repetição.
        var removidas = await Task.WhenAll(LimparAsync(fixture.A, batchSize: 2), LimparAsync(fixture.B, batchSize: 2));

        Assert.True(removidas.Sum() >= vencidas.Count, $"removidas={string.Join('+', removidas)}");
        var (sessoesVencidas, tokensVencidos, restantes) = await fixture.QueryAsync(async db => (
            await db.AuthSessions.CountAsync(s => vencidas.Contains(s.Id)),
            await db.RefreshTokens.CountAsync(t => vencidas.Contains(t.SessionId)),
            await db.AuthSessions.CountAsync(s => s.Id == naMargem || s.Id == ativa)));
        Assert.Equal(0, sessoesVencidas);
        Assert.Equal(0, tokensVencidos);
        Assert.Equal(2, restantes);
    }
}
