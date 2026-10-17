using System.Net.Http.Json;
using System.Text.Json;

namespace Almirante.Api.Tests;

public class ForwardedHeadersTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private record ConnectionInfo(string? RemoteIp, string Scheme);

    private static async Task<ConnectionInfo> GetConnectionInfoAsync(HttpClient client, string? xForwardedFor, string? xForwardedProto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ForwardedHeadersTestFactory.ConnectionInfoPath);
        if (xForwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", xForwardedFor);
        }

        if (xForwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", xForwardedProto);
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<ConnectionInfo>(JsonOptions);
        Assert.NotNull(body);
        return body!;
    }

    [Fact]
    public async Task SemRedeConfiavelConfigurada_XForwardedForEIgnorado()
    {
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: null, simulatedRemoteIp: "172.30.0.5");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "203.0.113.9");

        Assert.NotEqual("203.0.113.9", body.RemoteIp);
        Assert.Equal("172.30.0.5", body.RemoteIp);
    }

    [Fact]
    public async Task ConexaoImediataForaDaRedeConfiavel_XForwardedForEIgnorado()
    {
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: "172.30.0.0/24", simulatedRemoteIp: "203.0.113.50");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "203.0.113.9");

        Assert.NotEqual("203.0.113.9", body.RemoteIp);
        Assert.Equal("203.0.113.50", body.RemoteIp);
    }

    [Fact]
    public async Task ConexaoImediataDentroDaRedeConfiavel_UsaIpDoXForwardedFor()
    {
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: "172.30.0.0/24", simulatedRemoteIp: "172.30.0.5");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "203.0.113.9", xForwardedProto: "https");

        Assert.Equal("203.0.113.9", body.RemoteIp);
        Assert.Equal("https", body.Scheme);
    }

    [Fact]
    public async Task ConexaoImediataDentroDaRedeConfiavel_HeaderForjadoAEsquerdaEIgnorado()
    {
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: "172.30.0.0/24", simulatedRemoteIp: "172.30.0.5");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "1.1.1.1, 203.0.113.9");

        Assert.NotEqual("1.1.1.1", body.RemoteIp);
        Assert.Equal("203.0.113.9", body.RemoteIp);
    }
}
