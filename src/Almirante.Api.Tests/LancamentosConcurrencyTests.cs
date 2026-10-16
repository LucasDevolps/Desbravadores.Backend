using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentosConcurrencyTests
{
    [Fact]
    public async Task RegistrarTodos_ConcorrenciaPersisteUmaOperacao_EUmLancamentoPorMembro()
    {
        using var factory = new SqlServerApiFactory();
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        await TestHelpers.AddUsuarioAsync(factory, "Concorrência lançamentos", "DS");
        var membros = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync());
        var key = $"concorrente-{Guid.NewGuid():N}";

        async Task<HttpResponseMessage> Registrar(decimal valor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar")
            {
                Content = JsonContent.Create(new
                {
                    finalidade = "Evento", categoria = "Evento", tipoFluxo = "Saida", valor,
                    vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), aplicarATodosOsMembros = true,
                }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Registrar(25m)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var results = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<LancamentoGeralResponse>()));
        var operacaoId = Assert.Single(results.Select(r => r!.OperacaoId).Distinct());
        Assert.All(results, result => Assert.Equal(membros, result!.LancamentosCriados));
        var lancamentos = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking()
            .Where(l => l.OperacaoId == operacaoId).ToListAsync());
        Assert.Equal(membros, lancamentos.Count);
        Assert.Equal(membros, lancamentos.Select(l => l.MembroId).Distinct().Count());
        Assert.Equal(1, await TestHelpers.WithDbAsync(factory, db => db.LancamentosOperacoes.CountAsync(o => o.IdempotencyKey == key)));
        Assert.Equal(HttpStatusCode.Conflict, (await Registrar(26m)).StatusCode);
    }
}
