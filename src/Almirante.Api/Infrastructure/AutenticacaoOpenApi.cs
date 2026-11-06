using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Infrastructure;

/// <summary>Declara o Bearer só nas rotas que o exigem: as de [AllowAnonymous] funcionam sem token e ficam sem cadeado no Swagger UI.</summary>
public sealed class AutenticacaoOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) return;
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] }];
    }
}
