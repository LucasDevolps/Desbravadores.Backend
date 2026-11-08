using Almirante.Api.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Infrastructure;

public sealed class UsuariosFotoOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var controller = context.MethodInfo.DeclaringType;
        if ((controller != typeof(UsuariosController) && controller != typeof(MeController)) || context.MethodInfo.Name != nameof(UsuariosController.EnviarFoto))
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "Um único arquivo JPEG, PNG ou WebP no campo 'arquivo'.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { LeitorUploadFoto.Campo },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            [LeitorUploadFoto.Campo] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                        },
                    },
                },
            },
        };
    }
}
