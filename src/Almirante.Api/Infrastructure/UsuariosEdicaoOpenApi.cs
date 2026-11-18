using System.Text.Json.Nodes;
using Almirante.Api.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Infrastructure;

public sealed class UsuariosEdicaoParcialOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(UsuariosController) || context.MethodInfo.Name != nameof(UsuariosController.Patch)
            || operation.RequestBody?.Content is not { } content || !content.TryGetValue("application/json", out var json))
        {
            return;
        }

        json.Examples = new Dictionary<string, IOpenApiExample>
        {
            ["soTelefone"] = new OpenApiExample
            {
                Summary = "Só o telefone: nome, e-mail, cargo e os demais campos ficam como estão",
                Value = new JsonObject { ["telefone"] = "(11) 98765-4321" },
            },
            ["removerOpcional"] = new OpenApiExample
            {
                Summary = "null remove um campo opcional (aqui, doencasAlergias); nome, email e cargoId não aceitam null",
                Value = new JsonObject { ["doencasAlergias"] = null },
            },
        };
    }
}
