using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Almirante.Api.Tests;

public sealed partial class RastreabilidadeTests
{
    private static readonly string Matriz = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Docs", "rastreabilidade.md"));

    public static IReadOnlyList<string> EndpointsDaApi() => typeof(Program).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(ControllerBase)) && !t.IsAbstract)
        .SelectMany(t => t.GetMethods().Where(m => m.IsPublic && m.DeclaringType!.Assembly == typeof(Program).Assembly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>(false))
            .Select(a => $"{a.HttpMethods.Single()} /{t.GetCustomAttributes<RouteAttribute>(true).Single().Template}"
                + (string.IsNullOrEmpty(a.Template) ? "" : "/" + a.Template)))
        .Select(r => r.Replace(":guid", "", StringComparison.Ordinal))
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void TodoEndpointDaApiEstaNaMatriz()
    {
        var ausentes = EndpointsDaApi().Where(e => !Matriz.Contains($"`{e}`", StringComparison.Ordinal)).ToList();
        Assert.Empty(ausentes);
    }

    [Fact]
    public void MatrizNaoCitaEndpointQueNaoExiste()
    {
        var existentes = EndpointsDaApi().ToHashSet(StringComparer.Ordinal);
        var citados = LinhaDeEndpoint().Matches(Matriz).Select(m => m.Groups[1].Value).ToList();
        Assert.True(citados.Count >= existentes.Count, $"a matriz cita {citados.Count} endpoints e a API tem {existentes.Count}.");
        Assert.DoesNotContain(citados, c => !existentes.Contains(c));
    }

    [Fact]
    public void ClassesDeTesteCitadasExistem()
    {
        var classes = typeof(RastreabilidadeTests).Assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var citadas = ClasseCitada().Matches(Matriz).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(citadas);
        Assert.DoesNotContain(citadas, c => !classes.Contains(c));
    }

    [GeneratedRegex(@"^\| `((?:GET|POST|PUT|PATCH|DELETE) /api/[^`]+)`", RegexOptions.Multiline)]
    private static partial Regex LinhaDeEndpoint();

    [GeneratedRegex(@"`(\w+Tests)`")]
    private static partial Regex ClasseCitada();
}
