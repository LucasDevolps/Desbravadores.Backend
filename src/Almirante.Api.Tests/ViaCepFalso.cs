using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Almirante.Api.Tests;

/// <summary>
/// Handler primário do HttpClient do ViaCEP nos testes: nenhum teste chama a rede. Por padrão todo CEP existe (dados fictícios
/// determinísticos de <see cref="Exemplo"/>), exceto 00000000, que responde {"erro": "true"}.
/// </summary>
public sealed class ViaCepFalso : HttpMessageHandler
{
    public const string CepInexistente = "00000000";

    private readonly ConcurrentQueue<string> _consultas = new();

    public Func<string, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } = Padrao;

    /// <summary>CEPs consultados, na ordem, incluindo as novas tentativas do pipeline de resiliência.</summary>
    public IReadOnlyList<string> Consultas => [.. _consultas];

    public void Reiniciar()
    {
        _consultas.Clear();
        Responder = Padrao;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var segmentos = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("json", segmentos[^1]);
        var cep = segmentos[^2];
        _consultas.Enqueue(cep);
        return Responder(cep, cancellationToken);
    }

    public static Task<HttpResponseMessage> Padrao(string cep, CancellationToken _) =>
        Task.FromResult(cep == CepInexistente ? Json("""{ "erro": "true" }""") : Json(Exemplo(cep).ToJsonString()));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Resposta no formato do ViaCEP, com o CEP mascarado como o serviço devolve.</summary>
    public static JsonObject Exemplo(string cep) => new()
    {
        ["cep"] = $"{cep[..5]}-{cep[5..]}",
        ["logradouro"] = $"Rua Fictícia {cep}",
        ["complemento"] = "lado ímpar",
        ["unidade"] = "",
        ["bairro"] = "Jardim de Teste",
        ["localidade"] = "São Paulo",
        ["uf"] = "SP",
        ["estado"] = "São Paulo",
        ["regiao"] = "Sudeste",
        ["ibge"] = "3550308",
        ["gia"] = "1004",
        ["ddd"] = "11",
        ["siafi"] = "7107",
    };
}
