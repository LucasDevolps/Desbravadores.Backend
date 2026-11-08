using Microsoft.AspNetCore.HttpLogging;

namespace Almirante.Api.Security;

/// <summary>
/// Rota cujo corpo de requisição ou de resposta carrega senha ou token (issue #159). Toda resposta dela, inclusive 401/403/429 e erros,
/// sai com Cache-Control: no-store (middleware em Program.cs). Use sempre junto com [HttpLogging(<see cref="CamposRegistraveis"/>)]: o
/// HttpLogging do ASP.NET Core, se for habilitado, registra só método, rota e status. A auditoria HTTP (issue #158, AuditoriaHttpMiddleware)
/// grava nestas rotas apenas metadados e o marcador <see cref="MarcadorOmitido"/>, sem nunca copiar os corpos. Os DTOs dessas rotas também
/// redigem o próprio ToString().
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConteudoSensivelAttribute : Attribute
{
    public const string MarcadorOmitido = "[conteúdo sensível omitido]";

    public const HttpLoggingFields CamposRegistraveis = HttpLoggingFields.RequestProperties | HttpLoggingFields.ResponseStatusCode;

    public static bool Aplica(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<ConteudoSensivelAttribute>() is not null;
}
