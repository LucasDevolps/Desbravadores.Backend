using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>Apoio dos testes da issue #159: contas, sessões reais (CSRF ligado à identidade, cookie de refresh) e chamadas das rotas de senha.</summary>
public static class SenhasTestKit
{
    public const string RefreshCookie = "__Host-almirante-refresh";
    public const string CsrfCookie = "__Host-almirante-csrf";

    /// <summary>Limites altos: só os testes de rate limit usam os padrões.</summary>
    public static Dictionary<string, string?> SemLimites() => new()
    {
        ["Senhas:TentativasPorUsuario"] = "10000",
        ["Senhas:TentativasPorIp"] = "10000",
        ["Senhas:RecuperacoesPorIp"] = "10000",
    };

    public static string NovaSenha() => $"Cavalo-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}-Bateria";

    public sealed record Conta(Guid Id, string Email, string Senha);

    /// <summary>Sessão de navegador: Bearer, cookie de refresh e token CSRF emitido já para a identidade autenticada.</summary>
    public sealed record Sessao(HttpClient Client, string AccessToken, string Refresh, Conta Conta) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    public static async Task<Conta> ContaAsync(WebApplicationFactory<Program> factory, string role, string? senha = null, bool ativo = true)
    {
        senha ??= TestHelpers.SenhaPadraoTeste;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>();
        var cargoId = await db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync();
        var email = $"senha-{role.ToLowerInvariant()}-{Guid.NewGuid():N}@local.dev";
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(), Nome = $"Conta {role}", Email = email, EmailNormalizado = email.ToUpperInvariant(), SenhaHash = "", CargoId = cargoId,
            Ativo = ativo,
        };
        usuario.SenhaHash = hasher.HashPassword(usuario, senha);
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
        return new Conta(usuario.Id, email, senha);
    }

    public static HttpClient Cliente(WebApplicationFactory<Program> factory, string? ip = null)
    {
        var client = factory.CreateClient();
        if (ip is not null) client.DefaultRequestHeaders.Add(AlmiranteApiFactory.TestRemoteIpHeader, ip);
        return client;
    }

    public static async Task<HttpClient> AnonimoAsync(WebApplicationFactory<Program> factory, string? ip = null)
    {
        var client = Cliente(factory, ip);
        await TestHelpers.AddCsrfAsync(client);
        return client;
    }

    public static async Task<Sessao> LoginAsync(WebApplicationFactory<Program> factory, Conta conta, string? ip = null)
    {
        var client = await AnonimoAsync(factory, ip);
        using var resposta = await client.PostAsJsonAsync("/api/Auth/login", new { email = conta.Email, senha = conta.Senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var token = (await resposta.Content.ReadFromJsonAsync<LoginResponse>())!.Token.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await TestHelpers.AddCsrfAsync(client);
        return new Sessao(client, token, CookieDe(resposta, RefreshCookie)!, conta);
    }

    public static string? CookieDe(HttpResponseMessage resposta, string nome) =>
        resposta.Headers.TryGetValues("Set-Cookie", out var valores)
            ? valores.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(nome + "=", StringComparison.Ordinal))
                .Select(v => v[(nome.Length + 1)..]).LastOrDefault()
            : null;

    /// <summary>Repete um refresh com um cookie capturado, sem o cookie jar do cliente (que a troca própria já limpou).</summary>
    public static async Task<HttpStatusCode> RefreshComCookieAsync(WebApplicationFactory<Program> factory, string refresh) =>
        (await RefreshAsync(factory, refresh)).Status;

    /// <summary>Refresh anônimo como o navegador faz (sem Bearer, CSRF anônimo, só o cookie): devolve o access e o refresh novos.</summary>
    public static async Task<(HttpStatusCode Status, string? AccessToken, string? Refresh)> RefreshAsync(WebApplicationFactory<Program> factory, string refresh)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var csrf = await client.GetAsync("/api/Auth/csrf");
        var token = (await csrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Auth/refresh");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("Cookie", $"{CsrfCookie}={CookieDe(csrf, CsrfCookie)}; {RefreshCookie}={refresh}");
        using var resposta = await client.SendAsync(request);
        return resposta.IsSuccessStatusCode
            ? (resposta.StatusCode, (await resposta.Content.ReadFromJsonAsync<LoginResponse>())!.Token.AccessToken, CookieDe(resposta, RefreshCookie))
            : (resposta.StatusCode, null, null);
    }

    public static async Task<HttpStatusCode> MeAsync(WebApplicationFactory<Program> factory, string accessToken)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resposta = await client.GetAsync("/api/Auth/Me");
        return resposta.StatusCode;
    }

    public static async Task<HttpStatusCode> LoginStatusAsync(WebApplicationFactory<Program> factory, string email, string senha)
    {
        using var client = await AnonimoAsync(factory);
        using var resposta = await client.PostAsJsonAsync("/api/Auth/login", new { email, senha });
        return resposta.StatusCode;
    }

    public static Task<HttpResponseMessage> AlterarPropriaAsync(HttpClient client, string senhaAtual, string nova, string? confirmacao = null) =>
        client.PostAsJsonAsync("/api/Auth/alterar-senha",
            new { senhaAtualSolicitante = senhaAtual, novaSenha = nova, confirmacaoNovaSenha = confirmacao ?? nova });

    public static Task<HttpResponseMessage> AlterarDeUsuarioAsync(HttpClient client, Guid alvoId, string senhaResponsavel, string nova,
        string? confirmacao = null, string? metodo = null) =>
        client.PostAsJsonAsync($"/api/Usuarios/{alvoId}/alterar-senha",
            new { senhaAtualSolicitante = senhaResponsavel, novaSenha = nova, confirmacaoNovaSenha = confirmacao ?? nova, metodoVerificacao = metodo });

    public static Task<HttpResponseMessage> SolicitarAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/Auth/recuperacao-senha/solicitar", new { email });

    public static Task<HttpResponseMessage> EmitirAsync(HttpClient client, Guid alvoId, string senhaResponsavel, string? metodo = "AtendimentoPresencial") =>
        client.PostAsJsonAsync($"/api/Usuarios/{alvoId}/recuperacao-senha/autorizacao",
            new { senhaAtualSolicitante = senhaResponsavel, metodoVerificacao = metodo });

    public static async Task<AutorizacaoRecuperacaoSenhaResponse> EmitirOkAsync(HttpClient client, Guid alvoId, string senhaResponsavel)
    {
        using var resposta = await EmitirAsync(client, alvoId, senhaResponsavel);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<AutorizacaoRecuperacaoSenhaResponse>(NotificacoesTestKit.Json))!;
    }

    public static Task<HttpResponseMessage> ConfirmarAsync(HttpClient client, string token, string nova, string? confirmacao = null) =>
        client.PostAsJsonAsync("/api/Auth/recuperacao-senha/confirmar", new { token, novaSenha = nova, confirmacaoNovaSenha = confirmacao ?? nova });

    public sealed record EstadoConta(string SenhaHash, long SecurityVersion, int SessoesAtivas, int Auditorias, int Notificacoes, int Falhas, DateTime? Bloqueio);

    /// <summary>Tudo que uma operação recusada não pode mudar: credencial, versão, sessões, auditoria, notificações e bloqueio de login.</summary>
    public static Task<EstadoConta> EstadoAsync(WebApplicationFactory<Program> factory, Guid usuarioId) => TestHelpers.WithDbAsync(factory, async db =>
    {
        var u = await db.Usuarios.AsNoTracking().SingleAsync(x => x.Id == usuarioId);
        return new EstadoConta(u.SenhaHash, u.SecurityVersion,
            await db.AuthSessions.CountAsync(s => s.UsuarioId == usuarioId && s.RevokedAtUtc == null),
            await db.AlteracoesSenha.CountAsync(a => a.UsuarioAfetadoId == usuarioId),
            await db.Notificacoes.CountAsync(n => n.DestinatarioId == usuarioId),
            u.FalhasLoginConsecutivas, u.LoginBloqueadoAteUtc);
    });

    public static async Task<string> TituloAsync(HttpResponseMessage resposta) =>
        JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString()!;

    public static async Task<string[]> CamposComErroAsync(HttpResponseMessage resposta)
    {
        var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement;
        return json.TryGetProperty("errors", out var erros) ? erros.EnumerateObject().Select(e => e.Name).ToArray() : [];
    }

    /// <summary>Iterações gravadas num hash V3 do PasswordHasher (bytes 5-8, big-endian).</summary>
    public static int IteracoesDoHash(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(0x01, bytes[0]);
        return (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8];
    }

    public static void AssertNoStore(HttpResponseMessage resposta) =>
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString() ?? string.Empty);
}

/// <summary>
/// Executa uma ação (uma vez) imediatamente antes do comando SQL cujo texto contém o trecho armado, depois de deixar passar
/// <c>ignorar</c> ocorrências. Simula concorrência num ponto exato da operação; a ação recebe o comando, para agir na mesma conexão e
/// transação quando for preciso.
/// </summary>
public sealed class GatilhoComandoSql : DbCommandInterceptor
{
    private readonly Lock _lock = new();
    private string? _trecho;
    private int _ignorar;
    private Func<DbCommand, Task>? _acao;

    public void Armar(string trecho, Func<Task> acao, int ignorar = 0) => Armar(trecho, _ => acao(), ignorar);

    public void Armar(string trecho, Func<DbCommand, Task> acao, int ignorar = 0)
    {
        lock (_lock)
        {
            (_trecho, _acao, _ignorar) = (trecho, acao, ignorar);
        }
    }

    private async Task DispararAsync(DbCommand command)
    {
        Func<DbCommand, Task>? acao = null;
        lock (_lock)
        {
            if (_trecho is not null && command.CommandText.Contains(_trecho, StringComparison.Ordinal) && _ignorar-- == 0)
            {
                (acao, _trecho) = (_acao, null);
            }
        }

        if (acao is not null)
        {
            await acao(command);
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await DispararAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await DispararAsync(command);
        return result;
    }
}
