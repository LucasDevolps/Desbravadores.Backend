using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Almirante.Api.Dtos;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.IdentityModel.Tokens;

namespace Almirante.Api.Tests;

public static class NotificacoesTestKit
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Vencimento => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd");

    public static Task<HttpResponseMessage> RegistrarDebitoAsync(HttpClient client, Guid membroId, string tipoFluxo = "Entrada",
        decimal valor = 50m, string? chave = null, string? descricao = "Outubro", string finalidade = "Mensalidade")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar")
        {
            Content = JsonContent.Create(new
            {
                membroId, finalidade, descricao, categoria = "Clube", tipoFluxo, valor, vencimento = Vencimento, aplicarATodosOsMembros = false,
            }),
        };
        if (chave is not null) request.Headers.Add("Idempotency-Key", chave);
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> RegistrarGeralAsync(HttpClient client, string chave, string tipoFluxo = "Entrada", decimal valor = 30m)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Registrar")
        {
            Content = JsonContent.Create(new
            {
                finalidade = "Uniflash", descricao = "Camiseta", categoria = "Clube", tipoFluxo, valor, vencimento = Vencimento, aplicarATodosOsMembros = true,
            }),
        };
        request.Headers.Add("Idempotency-Key", chave);
        return client.SendAsync(request);
    }

    public static async Task<NotificacoesPendentesResponse> PendentesAsync(HttpClient client, int? pageSize = null, string? cursor = null)
    {
        var query = new List<string>();
        if (pageSize is not null) query.Add($"pageSize={pageSize}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var response = await client.GetAsync("/api/Notificacoes/Pendentes" + (query.Count > 0 ? "?" + string.Join('&', query) : ""));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<NotificacoesPendentesResponse>(Json))!;
    }

    public static Task<HttpResponseMessage> LerAsync(HttpClient client, Guid notificacaoId) =>
        client.PostAsync($"/api/Notificacoes/{notificacaoId}/Leitura", content: null);

    public static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    /// <summary>Long polling pelo TestServer: o token vai no cabeçalho, como no cliente .NET.</summary>
    public static HubConnection Hub(WebApplicationFactory<Program> factory, string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/notificacoes"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    /// <summary>WebSocket como o navegador faz: negotiate com cabeçalho e o handshake GET com access_token na query.</summary>
    public static HubConnection HubWebSocketComTokenNaQuery(WebApplicationFactory<Program> factory, string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/notificacoes"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.WebSocketFactory = async (context, ct) =>
                {
                    var cliente = factory.Server.CreateWebSocketClient();
                    var uri = new UriBuilder(context.Uri) { Query = context.Uri.Query.TrimStart('?') + "&access_token=" + Uri.EscapeDataString(token) }.Uri;
                    return await cliente.ConnectAsync(uri, ct);
                };
            })
            .Build();

    /// <summary>Token de acesso válido para uma sessão existente, com expiração curta (o handler aceita 30 s de tolerância).</summary>
    public static string TokenComExpiracao(AlmiranteApiFactory factory, string tokenOriginal, TimeSpan duracao) =>
        TokenReassinado(factory, tokenOriginal, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow + duracao);

    /// <summary>Mesmo sub/role/sid do token original, assinado com a chave real, com datas escolhidas (ex.: já expirado).</summary>
    public static string TokenReassinado(AlmiranteApiFactory factory, string tokenOriginal, DateTime emitidoEmUtc, DateTime expiraEmUtc)
    {
        var original = new JwtSecurityTokenHandler().ReadJwtToken(tokenOriginal);
        var chave = new SymmetricSecurityKey(Convert.FromBase64String(factory.JwtKeyBase64)) { KeyId = factory.JwtKeyId };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "Almirante.Api.Tests", Audience = "Almirante.Api.Tests",
            NotBefore = emitidoEmUtc, IssuedAt = emitidoEmUtc, Expires = expiraEmUtc,
            Subject = new ClaimsIdentity([
                new(JwtRegisteredClaimNames.Sub, original.Subject), new("role", original.Claims.Single(c => c.Type == "role").Value),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()), new("sid", original.Claims.Single(c => c.Type == "sid").Value)]),
            SigningCredentials = new SigningCredentials(chave, SecurityAlgorithms.HmacSha256),
            TokenType = "at+jwt",
        };
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public static Guid Sessao(string token) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(c => c.Type == "sid").Value);
}

/// <summary>Coleta os eventos do hub recebidos por uma conexão.</summary>
public sealed class AvisosRecebidos
{
    private readonly ConcurrentQueue<(string Evento, JsonElement Payload)> _recebidos = new();
    private readonly SemaphoreSlim _novo = new(0);

    public AvisosRecebidos(HubConnection conexao)
    {
        conexao.On<JsonElement>("NotificacaoCriada", p => Guardar("NotificacaoCriada", p));
        conexao.On<JsonElement>("NotificacaoLida", p => Guardar("NotificacaoLida", p));
    }

    public IReadOnlyList<(string Evento, JsonElement Payload)> Todos => _recebidos.ToArray();

    private void Guardar(string evento, JsonElement payload)
    {
        _recebidos.Enqueue((evento, payload.Clone()));
        _novo.Release();
    }

    public async Task<JsonElement> AguardarAsync(string evento, Func<JsonElement, bool>? filtro = null, TimeSpan? limite = null)
    {
        var fim = DateTime.UtcNow + (limite ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            foreach (var (nome, payload) in _recebidos)
            {
                if (nome == evento && (filtro?.Invoke(payload) ?? true)) return payload;
            }

            var restante = fim - DateTime.UtcNow;
            if (restante <= TimeSpan.Zero || !await _novo.WaitAsync(restante))
            {
                throw new TimeoutException($"Evento {evento} não recebido. Recebidos: {string.Join(", ", Todos.Select(r => r.Evento))}.");
            }
        }
    }

    public static Guid IdCriada(JsonElement payload) => payload.GetProperty("notificacao").GetProperty("id").GetGuid();
}
