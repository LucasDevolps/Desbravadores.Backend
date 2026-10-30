using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Almirante.Api.Tests;

/// <summary>
/// O swagger.json descreve a API real (issue #160): toda rota aparece com resumo, o Bearer é exigido exatamente nas rotas
/// autenticadas e os códigos que os testes observam nas rotas corrigidas pela auditoria continuam declarados.
/// </summary>
public sealed partial class SwaggerContratoTests
{
    private static readonly string[] RotasAnonimas =
    [
        "GET /api/Auth/csrf",
        "POST /api/Auth/login",
        "POST /api/Auth/logout",
        "POST /api/Auth/recuperacao-senha/confirmar",
        "POST /api/Auth/recuperacao-senha/solicitar",
        "POST /api/Auth/refresh",
    ];

    [Fact]
    public async Task TodoEndpointDaApiEstaNoSwaggerComResumo()
    {
        var operacoes = await OperacoesAsync();

        var esperados = RastreabilidadeTests.EndpointsDaApi().Select(Normalizar).Order(StringComparer.Ordinal);
        Assert.Equal(esperados, operacoes.Keys.Select(Normalizar).Order(StringComparer.Ordinal));
        Assert.Empty(operacoes.Where(o => string.IsNullOrWhiteSpace((string?)o.Value["summary"])).Select(o => o.Key));
    }

    [Fact]
    public async Task BearerEhExigidoSoNasRotasAutenticadas()
    {
        var operacoes = await OperacoesAsync();

        var semBearer = operacoes.Where(o => o.Value["security"]?[0]?["Bearer"] is null).Select(o => o.Key).Order(StringComparer.Ordinal);
        Assert.Equal(RotasAnonimas, semBearer);
    }

    [Theory]
    [InlineData("PUT /api/Lancamentos/{id}", 409)]
    [InlineData("DELETE /api/Lancamentos/{id}", 409)]
    [InlineData("GET /api/Classes/{classe}", 400)]
    [InlineData("GET /api/Usuarios", 400)]
    [InlineData("POST /api/Eventos", 415)]
    public async Task CodigoObservadoNosTestesEstaDeclarado(string rota, int status)
    {
        var operacoes = await OperacoesAsync();

        Assert.NotNull(operacoes[rota]["responses"]![status.ToString()]);
    }

    private static async Task<Dictionary<string, JsonNode>> OperacoesAsync()
    {
        await using var factory = new ConfiguredApiFactory("Development", new Dictionary<string, string?> { ["AllowedHosts"] = "*" });
        var documento = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json"))!;
        return documento["paths"]!.AsObject()
            .SelectMany(p => p.Value!.AsObject()
                .Where(o => o.Key is "get" or "post" or "put" or "delete")
                .Select(o => (Rota: $"{o.Key.ToUpperInvariant()} {p.Key}", Operacao: o.Value!)))
            .ToDictionary(o => o.Rota, o => o.Operacao);
    }

    private static string Normalizar(string rota) => ParametroDeRota().Replace(rota, "{}");

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex ParametroDeRota();
}
