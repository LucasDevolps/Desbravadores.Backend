using System.Net.Http.Json;
using System.Text.Json;

namespace Almirante.Api.Tests;

// Valida a configuração do ForwardedHeadersMiddleware feita em Program.cs: a API só deve
// confiar em X-Forwarded-For/X-Forwarded-Proto quando a conexão imediata vier da rede
// configurada em ReverseProxy:TrustedNetworkCidr (o nginx, no Docker Compose, na subnet
// 172.30.0.0/24 usada em compose.yaml/.env.example). Sem essa configuração (cenário atual do
// IIS) ou quando a conexão imediata não pertence a essa rede, o header não deve ser aceito.
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
        // Cenário fora do Docker (ex.: IIS): nenhuma rede confiável configurada.
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: null, simulatedRemoteIp: "172.30.0.5");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "203.0.113.9");

        Assert.NotEqual("203.0.113.9", body.RemoteIp);
        Assert.Equal("172.30.0.5", body.RemoteIp);
    }

    [Fact]
    public async Task ConexaoImediataForaDaRedeConfiavel_XForwardedForEIgnorado()
    {
        // Rede confiável configurada (como no Docker), mas a conexão chegou de fora dela —
        // equivalente a alguém acessando a API diretamente, contornando o nginx.
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: "172.30.0.0/24", simulatedRemoteIp: "203.0.113.50");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "203.0.113.9");

        Assert.NotEqual("203.0.113.9", body.RemoteIp);
        Assert.Equal("203.0.113.50", body.RemoteIp);
    }

    [Fact]
    public async Task ConexaoImediataDentroDaRedeConfiavel_UsaIpDoXForwardedFor()
    {
        // Conexão imediata dentro da rede confiável (equivalente ao nginx no Docker):
        // o IP e o protocolo informados pelo proxy passam a ser usados.
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: "172.30.0.0/24", simulatedRemoteIp: "172.30.0.5");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "203.0.113.9", xForwardedProto: "https");

        Assert.Equal("203.0.113.9", body.RemoteIp);
        Assert.Equal("https", body.Scheme);
    }

    [Fact]
    public async Task ConexaoImediataDentroDaRedeConfiavel_HeaderForjadoAEsquerdaEIgnorado()
    {
        // Simula o encadeamento produzido pelo nginx (proxy_add_x_forwarded_for): um valor
        // forjado pelo cliente fica mais à esquerda, e o valor real observado pelo nginx é
        // anexado à direita. ForwardLimit=1 garante que só o valor mais à direita é aceito.
        using var factory = new ForwardedHeadersTestFactory(trustedNetworkCidr: "172.30.0.0/24", simulatedRemoteIp: "172.30.0.5");
        using var client = factory.CreateClient();

        var body = await GetConnectionInfoAsync(client, xForwardedFor: "1.1.1.1, 203.0.113.9");

        Assert.NotEqual("1.1.1.1", body.RemoteIp);
        Assert.Equal("203.0.113.9", body.RemoteIp);
    }
}
