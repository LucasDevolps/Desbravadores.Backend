using System.Reflection;
using System.Text.Json.Nodes;
using Almirante.Api.Controllers;
using Almirante.Api.Dtos;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Infrastructure;

// Documentação OpenAPI de /api/Eventos: o Swashbuckle não representa sozinho um campo que aceita
// "GUID | GUID[]" (conversor customizado), então o schema de "membros" é declarado explicitamente
// como oneOf; os exemplos mostram as duas formas.

/// <summary>Declara <c>membros</c> como <c>oneOf [GUID, GUID[]]</c>.</summary>
public sealed class MembrosSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        var converter = context.MemberInfo?.GetCustomAttribute<System.Text.Json.Serialization.JsonConverterAttribute>();
        if (converter?.ConverterType != typeof(MembrosJsonConverter) || schema is not OpenApiSchema openApi)
        {
            return;
        }

        openApi.Type = null;
        openApi.Format = null;
        openApi.Items = null;
        openApi.Description = "GUID | GUID[]: IDs de Usuarios.Id. Aceita um único GUID (string) ou um array de GUIDs; " +
            "após a desserialização os dois formatos são idênticos. Proibidos: null, array vazio, GUID vazio/inválido, repetidos e inexistentes. " +
            "A resposta devolve sempre um array.";
        openApi.OneOf =
        [
            new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Examples = [JsonValue.Create("11111111-1111-4111-8111-111111111111")] },
            new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                MinItems = 1,
                MaxItems = MembrosJsonConverter.MaximoMembros,
                UniqueItems = true,
                Items = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
            },
        ];
    }
}

/// <summary>Resumo/descrição, exemplos de corpo (um membro e vários) e o header Idempotency-Key.</summary>
public sealed class EventosOperationFilter : IOperationFilter
{
    private const string UmMembro = "11111111-1111-4111-8111-111111111111";
    private const string DoisMembros = "22222222-2222-4222-8222-222222222222";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is not Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor action
            || action.ControllerTypeInfo.AsType() != typeof(EventosController))
        {
            return;
        }

        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        operation.Summary ??= metadata.OfType<IEndpointSummaryMetadata>().FirstOrDefault()?.Summary;
        operation.Description ??= metadata.OfType<IEndpointDescriptionMetadata>().FirstOrDefault()?.Description;

        var idempotency = operation.Parameters?.OfType<OpenApiParameter>().FirstOrDefault(p => p.Name == "Idempotency-Key");
        if (idempotency is not null)
        {
            idempotency.Required = true;
            idempotency.Description = "UUID desta operação (obrigatório). Mesma chave + mesma operação normalizada devolve 200 com o resultado original; " +
                "mesma chave com dados diferentes retorna 409; ausente ou inválida retorna 400. O escopo é o usuário autenticado.";
        }

        if (operation.RequestBody?.Content is not { } content || !content.TryGetValue("application/json", out var json))
        {
            return;
        }

        var verbo = context.ApiDescription.HttpMethod;
        if (verbo == "POST")
        {
            json.Examples = new Dictionary<string, IOpenApiExample>
            {
                ["umMembro"] = Exemplo("Um membro (membros como GUID)", Corpo(post: true, membros: JsonValue.Create(UmMembro))),
                ["variosMembros"] = Exemplo("Vários membros (membros como GUID[])", Corpo(post: true, membros: Membros())),
            };
        }
        else if (verbo == "PUT")
        {
            json.Examples = new Dictionary<string, IOpenApiExample>
            {
                ["umMembro"] = Exemplo("Um membro (membros como GUID)", Corpo(post: false, membros: JsonValue.Create(UmMembro))),
                ["variosMembros"] = Exemplo("Vários membros (membros como GUID[])", Corpo(post: false, membros: Membros())),
            };
        }
        else if (verbo == "DELETE")
        {
            json.Examples = new Dictionary<string, IOpenApiExample>
            {
                ["exclusao"] = Exemplo("Exclusão lógica", new JsonObject { ["motivo"] = "Grupo cancelado pela diretoria", ["versao"] = "AAAAAAAAB9E=" }),
            };
        }
    }

    private static OpenApiExample Exemplo(string resumo, JsonNode valor) => new() { Summary = resumo, Value = valor };

    private static JsonArray Membros() => new(JsonValue.Create(UmMembro), JsonValue.Create(DoisMembros));

    private static JsonObject Corpo(bool post, JsonNode membros)
    {
        var corpo = new JsonObject
        {
            ["dataEvento"] = "2026-10-18",
            ["local"] = "Parque Ibirapuera",
            ["transporte"] = new JsonObject { ["valor"] = 10.00m, ["ehGratis"] = false },
            ["alimentacao"] = new JsonObject { ["individual"] = false, ["valor"] = 8.00m },
            ["seguroObrigatorio"] = 2.00m,
            ["membros"] = membros,
        };
        if (post)
        {
            corpo["eventoReferenciaId"] = null;
        }
        else
        {
            corpo["versao"] = "AAAAAAAAB9E=";
            corpo["motivo"] = "Obrigatório apenas quando a alteração remove participantes";
        }

        return corpo;
    }
}
