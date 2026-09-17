using System.Reflection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Security;

// Documenta no OpenAPI o header X-CSRF-TOKEN exigido por OperacaoComCookie(ValidarCsrf = true).
public sealed class CsrfHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var cookie = context.MethodInfo.GetCustomAttribute<OperacaoComCookieAttribute>();
        if (cookie is null) return;

        operation.Description = string.Join(" ", new[]
        {
            operation.Description,
            "Aceita somente HTTPS. Usa o cookie HttpOnly __Host-almirante-refresh/__Host-almirante-csrf, então chame com credentials: \"include\".",
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (!cookie.ValidarCsrf) return;
        operation.Parameters ??= new List<IOpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-CSRF-TOKEN",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Token obtido em GET /api/Auth/csrf (mantido só em memória). Vale com ou sem Authorization.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }
}
