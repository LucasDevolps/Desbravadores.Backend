using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

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
