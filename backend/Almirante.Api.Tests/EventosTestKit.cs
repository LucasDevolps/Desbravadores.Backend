using System.Net.Http.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Tests;

// Utilidades compartilhadas pelos testes de /api/Eventos.
public static class EventosTestKit
{
    public static DateOnly Hoje => DateOnly.FromDateTime(DateTime.UtcNow);
    public static DateOnly PrimeiroDiaDoMes => new(Hoje.Year, Hoje.Month, 1);

    // "membros" recebe QUALQUER objeto (Guid, Guid[], string, null...) para exercitar os dois formatos e os inválidos.
    public static Dictionary<string, object?> Corpo(object? membros, DateOnly? data = null, string local = "Parque Ibirapuera",
        decimal transporte = 10m, bool gratis = false, decimal alimentacao = 8m, bool individual = false, decimal seguro = 2m,
        Guid? referencia = null, string? versao = null, string? motivo = null)
    {
        var corpo = new Dictionary<string, object?>
        {
            ["dataEvento"] = (data ?? Hoje.AddDays(10)).ToString("yyyy-MM-dd"),
            ["local"] = local,
            ["transporte"] = new { valor = transporte, ehGratis = gratis },
            ["alimentacao"] = new { individual, valor = alimentacao },
            ["seguroObrigatorio"] = seguro,
            ["membros"] = membros,
        };
        if (versao is null)
        {
            corpo["eventoReferenciaId"] = referencia;
        }
        else
        {
            corpo["versao"] = versao;
            if (motivo is not null) corpo["motivo"] = motivo;
        }

        return corpo;
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, object body, string? chave = null, bool semChave = false, string? remoteIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Eventos") { Content = JsonContent.Create(body) };
        if (!semChave) request.Headers.Add("Idempotency-Key", chave ?? Guid.NewGuid().ToString());
        if (remoteIp is not null) request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, remoteIp);
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostJsonBrutoAsync(HttpClient client, string json, string? chave = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Eventos")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", chave ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, object body) =>
        client.PutAsJsonAsync($"/api/Eventos/{id}", body);

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id, string? motivo, string? versao, string? remoteIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Eventos/{id}")
        {
            Content = JsonContent.Create(new { motivo, versao }),
        };
        if (remoteIp is not null) request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, remoteIp);
        return client.SendAsync(request);
    }

    public static async Task<EventoDto> LerAsync(HttpResponseMessage response)
    {
        var texto = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<EventoDto>(texto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException(texto);
    }
}
