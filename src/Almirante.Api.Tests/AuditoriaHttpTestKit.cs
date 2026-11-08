using Almirante.Api.Entities;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>Apoio aos testes da auditoria HTTP (issue #158).</summary>
public static class AuditoriaHttpTestKit
{
    public const string LentoPath = "/__tests/auditoria/lento";
    public const string LentoAntesDeResponderPath = "/__tests/auditoria/lento-antes";

    /// <summary>
    /// Registros gravados, depois de forçar o gravador (o registro sai no OnCompleted, que pode rodar logo depois de o cliente receber a
    /// resposta): espera até haver <paramref name="minimo"/> registros que passam no filtro.
    /// </summary>
    public static async Task<List<AuditoriaHttp>> RegistrosAsync(WebApplicationFactory<Program> factory, Func<AuditoriaHttp, bool>? filtro = null,
        int minimo = 1, int prazoMs = 10_000)
    {
        var gravador = factory.Services.GetRequiredService<GravadorAuditoriaHttp>();
        var limite = DateTime.UtcNow.AddMilliseconds(prazoMs);
        while (true)
        {
            await gravador.GravarPendentesAsync(CancellationToken.None);
            var todos = await TestHelpers.WithDbAsync(factory, db => db.AuditoriasHttp.AsNoTracking().ToListAsync());
            var registros = todos.Where(filtro ?? (_ => true)).OrderBy(r => r.DataRequestUtc).ToList();
            if (registros.Count >= minimo || DateTime.UtcNow > limite)
            {
                return registros;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>O registro da única chamada cujo caminho (sem query) termina como indicado.</summary>
    public static async Task<AuditoriaHttp> RegistroAsync(WebApplicationFactory<Program> factory, string metodo, string sufixoCaminho)
    {
        var registros = await RegistrosAsync(factory, r => r.MetodoHttp == metodo && Caminho(r).EndsWith(sufixoCaminho, StringComparison.OrdinalIgnoreCase));
        return Assert.Single(registros);
    }

    public static string Caminho(AuditoriaHttp registro)
    {
        var url = registro.Url;
        var barra = url.IndexOf('/', url.IndexOf("://", StringComparison.Ordinal) + 3);
        if (barra < 0)
        {
            return "/";
        }

        var query = url.IndexOf('?', barra);
        return query < 0 ? url[barra..] : url[barra..query];
    }

    /// <summary>Todas as colunas de texto juntas, para provar que um valor sensível não chegou a nenhum destino.</summary>
    public static string Tudo(AuditoriaHttp r) =>
        string.Join("\n", r.Url, r.MetodoHttp, r.Request, r.Response, r.Headers, r.Exception, r.IpUsuario, r.IdentificacaoUsuario, r.TraceId);

    public static void AssertSemSegredos(IEnumerable<AuditoriaHttp> registros, params string[] segredos)
    {
        foreach (var registro in registros)
        {
            AuditoriaHttpSanitizadorTests.AssertSemSegredos(Tudo(registro), segredos);
        }
    }

    /// <summary>Endpoint de teste que espera até o cliente desistir (cancelamento/desconexão).</summary>
    public sealed class EndpointLento : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, proximo) =>
            {
                if (context.Request.Path == LentoPath)
                {
                    await context.Response.WriteAsJsonAsync(new { status = "Iniciado" }, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                    return;
                }

                if (context.Request.Path == LentoAntesDeResponderPath)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                    return;
                }

                await proximo(context);
            });
        };
    }
}
