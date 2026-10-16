using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

// Fábrica de testes dedicada para validar a configuração do ForwardedHeadersMiddleware
// (Program.cs). Não expõe nenhum endpoint público de produção: o endpoint de diagnóstico e a
// simulação da conexão TCP imediata são registrados só aqui, exclusivamente para estes testes.
public class ForwardedHeadersTestFactory(string? trustedNetworkCidr, string simulatedRemoteIp) : AlmiranteApiFactory
{
    public const string ConnectionInfoPath = "/__tests/connection-info";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:TrustedNetworkCidr"] = trustedNetworkCidr,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(
                new ConnectionInfoStartupFilter(IPAddress.Parse(simulatedRemoteIp)));
        });
    }

    // O TestServer em memória não tem uma conexão TCP real, então HttpContext.Connection.RemoteIpAddress
    // fica nulo por padrão. Este filtro simula, só nos testes, a conexão imediata que no Docker seria a
    // do container do nginx — e reporta o resultado final após o pipeline real (incluindo
    // UseForwardedHeaders) processar a requisição.
    private class ConnectionInfoStartupFilter(IPAddress simulatedRemoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = simulatedRemoteIp;
                    await nextMiddleware(context);
                });

                // Resto do pipeline real de Program.cs, incluindo app.UseForwardedHeaders().
                next(app);

                app.Run(async context =>
                {
                    await context.Response.WriteAsJsonAsync(new ConnectionInfo(
                        context.Connection.RemoteIpAddress?.ToString(),
                        context.Request.Scheme));
                });
            };
        }
    }

    private record ConnectionInfo(string? RemoteIp, string Scheme);
}
