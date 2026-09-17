using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Almirante.Api.Tests;

// Cookie jar explícito para os testes de fluxo: permite repetir um cookie antigo (replay), copiar a
// sessão entre duas instâncias da API e inspecionar Set-Cookie — coisas que o CookieContainer do
// WebApplicationFactory esconde.
public sealed class CookieJar
{
    public const string RefreshCookie = "__Host-almirante-refresh";
    public const string CsrfCookie = "__Host-almirante-csrf";

    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public string? this[string name]
    {
        get => _cookies.GetValueOrDefault(name);
        set { if (value is null) _cookies.Remove(name); else _cookies[name] = value; }
    }

    public CookieJar Clone()
    {
        var clone = new CookieJar();
        foreach (var (name, value) in _cookies) clone._cookies[name] = value;
        return clone;
    }

    public void ApplyTo(HttpRequestMessage request)
    {
        if (_cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
    }

    public void StoreFrom(HttpResponseMessage response)
    {
        foreach (var setCookie in SetCookies(response))
        {
            var (name, value, attributes) = Parse(setCookie);
            var expires = attributes.GetValueOrDefault("expires");
            var expired = expires is not null && DateTimeOffset.TryParse(expires, out var when) && when <= DateTimeOffset.UtcNow;
            this[name] = expired || value.Length == 0 ? null : value;
        }
    }

    public static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    public static (string Name, string Value, Dictionary<string, string?> Attributes) Parse(string setCookie)
    {
        var parts = setCookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var nameValue = parts[0].Split('=', 2);
        var attributes = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : null);
        return (nameValue[0], nameValue.Length > 1 ? nameValue[1] : "", attributes);
    }
}

// Passos do contrato documentado em docs/authentication-security.md, sem atalhos de teste.
public static class AuthFlow
{
    public static async Task<string> GetCsrfAsync(HttpClient client, CookieJar jar, string? bearer = null)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/Auth/csrf", jar, bearer: bearer);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("csrfToken").GetString()!;
    }

    public static async Task<string> LoginAsync(HttpClient client, CookieJar jar, string email, string senha)
    {
        var csrf = await GetCsrfAsync(client, jar);
        using var response = await SendAsync(client, HttpMethod.Post, "/api/Auth/login", jar, csrf, body: new { email, senha });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetProperty("accessToken").GetString()!;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, CookieJar? jar = null,
        string? csrf = null, string? bearer = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        jar?.ApplyTo(request);
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        jar?.StoreFrom(response);
        return response;
    }

    public static Task<HttpResponseMessage> RefreshAsync(HttpClient client, CookieJar jar, string csrf, string? bearer = null) =>
        SendAsync(client, HttpMethod.Post, "/api/Auth/refresh", jar, csrf, bearer);

    public static Task<HttpResponseMessage> LogoutAsync(HttpClient client, CookieJar jar, string csrf, string? bearer = null) =>
        SendAsync(client, HttpMethod.Post, "/api/Auth/logout", jar, csrf, bearer);

    public static Task<HttpResponseMessage> MeAsync(HttpClient client, string? bearer) =>
        SendAsync(client, HttpMethod.Get, "/api/Auth/Me", bearer: bearer);

    public static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetProperty("accessToken").GetString()!;
    }

    public static Guid SessionId(string accessToken) => Guid.Parse(new JsonWebToken(accessToken).GetClaim("sid").Value);
}
