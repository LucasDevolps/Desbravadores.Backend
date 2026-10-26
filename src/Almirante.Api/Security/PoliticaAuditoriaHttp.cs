using System.Text;

namespace Almirante.Api.Security;

/// <summary>
/// Decisões da auditoria HTTP que dependem da API em execução (issue #158): o que fica de fora (login), o que é rota de credenciais
/// (só metadados) e quais segmentos literais de caminho existem nas rotas mapeadas (o resto de um caminho desconhecido vira marcador).
/// </summary>
public sealed class PoliticaAuditoriaHttp(EndpointDataSource endpoints)
{
    public const string CaminhoLogin = "/api/auth/login";
    public const string PrefixoCredenciais = "/api/auth";

    /// <summary>Arquivos estáticos do Swagger UI (quando habilitado), que não são endpoints roteados.</summary>
    private static readonly string[] LiteraisSwagger =
    [
        "swagger", "v1", "swagger.json", "index.html", "index.js", "index.css", "swagger-ui.css", "swagger-ui-bundle.js",
        "swagger-ui-standalone-preset.js", "oauth2-redirect.html", "oauth2-redirect.js", "favicon-16x16.png", "favicon-32x32.png",
    ];

    private readonly Lazy<HashSet<string>> _literais = new(() =>
    {
        var literais = new HashSet<string>(LiteraisSwagger, StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            foreach (var parte in endpoint.RoutePattern.PathSegments.SelectMany(s => s.Parts))
            {
                if (parte is Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart literal)
                {
                    literais.Add(literal.Content);
                }
            }
        }

        return literais;
    });

    public bool LiteralConhecido(string segmento) => _literais.Value.Contains(segmento);

    /// <summary>Login: excluído por endpoint ([SemAuditoriaHttp]) e por caminho normalizado, em qualquer método.</summary>
    public static bool Excluida(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<SemAuditoriaHttpAttribute>() is not null
        || Normalizar(context.Request.Path.Value) == CaminhoLogin;

    /// <summary>/api/Auth inteiro (refresh, CSRF, logout, Me, senhas) e rotas [ConteudoSensivel]: só metadados e marcadores.</summary>
    public static bool RotaDeCredenciais(HttpContext context)
    {
        var caminho = Normalizar(context.Request.Path.Value);
        return caminho == PrefixoCredenciais || caminho.StartsWith(PrefixoCredenciais + "/", StringComparison.Ordinal)
            || ConteudoSensivelAttribute.Aplica(context);
    }

    /// <summary>Minúsculas, barras repetidas reduzidas e sem barra final: /API//Auth/Login/ e /api/auth/login são o mesmo caminho.</summary>
    public static string Normalizar(string? caminho)
    {
        if (string.IsNullOrEmpty(caminho))
        {
            return "/";
        }

        var saida = new StringBuilder(caminho.Length);
        foreach (var c in caminho.Trim())
        {
            if (c == '/' && saida.Length > 0 && saida[^1] == '/')
            {
                continue;
            }

            saida.Append(char.ToLowerInvariant(c));
        }

        while (saida.Length > 1 && (saida[^1] == '/' || saida[^1] == '.' || char.IsWhiteSpace(saida[^1])))
        {
            saida.Length--;
        }

        return saida.ToString();
    }
}
