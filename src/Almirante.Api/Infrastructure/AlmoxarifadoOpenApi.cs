using System.Text.Json.Nodes;
using Almirante.Api.Controllers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Almirante.Api.Infrastructure;

/// <summary>Resumo/descrição, header Idempotency-Key do atendimento e exemplos de corpo das rotas do almoxarifado (issue #126).</summary>
public sealed class AlmoxarifadoOperationFilter : IOperationFilter
{
    private const string Membro = "11111111-1111-4111-8111-111111111111";
    private const string Versao = "AAAAAAAAB9E=";

    private static readonly Dictionary<string, Dictionary<string, (string Resumo, Func<JsonObject> Corpo)>> Exemplos = new()
    {
        ["POST api/Almoxarifado/Bens"] = new()
        {
            ["comValorComercial"] = ("Barraca com valor comercial", () => new JsonObject
            {
                ["nome"] = "Barraca 4 pessoas", ["descricao"] = "Barraca iglu verde, com sobreteto", ["categoria"] = "Acampamento",
                ["estadoConservacao"] = "Bom", ["possuiValorComercial"] = true, ["valorEstimado"] = 350.00m, ["dataAquisicao"] = "2025-03-10",
                ["origem"] = "Compra", ["localizacao"] = "Almoxarifado - prateleira 2", ["responsavelGuardaId"] = Membro, ["marca"] = "Nautika",
                ["valorAquisicao"] = 420.00m,
            }),
            ["semValorComercial"] = ("Troféu sem valor comercial", () => new JsonObject
            {
                ["nome"] = "Troféu Campori 2019", ["descricao"] = "Troféu de 1º lugar em ordem unida", ["categoria"] = "Troféus",
                ["estadoConservacao"] = "Bom", ["possuiValorComercial"] = false, ["valorEstimado"] = null, ["dataAquisicao"] = "2019-11-15",
                ["origem"] = "Outro", ["origemDetalhe"] = "Premiação do Campori da Associação", ["localizacao"] = "Sala do clube - estante",
            }),
            ["acervoSemData"] = ("Livro de atas antigo, sem data conhecida", () => new JsonObject
            {
                ["nome"] = "Livro de atas 1998-2004", ["descricao"] = "Livro de atas da diretoria", ["estadoConservacao"] = "Regular",
                ["possuiValorComercial"] = false, ["dataAquisicaoDesconhecida"] = true,
                ["justificativaDataDesconhecida"] = "Acervo recebido da gestão anterior, sem registro de aquisição", ["origem"] = "Doacao",
                ["localizacao"] = "Secretaria - arquivo",
            }),
        },
        ["POST api/Almoxarifado/Bens/{id}/Emprestimos"] = new()
        {
            ["emprestimo"] = ("Empréstimo a um membro", () => new JsonObject
            {
                ["membroId"] = Membro, ["dataRetirada"] = "2026-10-20", ["previsaoDevolucao"] = "2026-10-27", ["conservacaoSaida"] = "Bom",
                ["observacoes"] = "Acampamento de unidade",
            }),
        },
        ["POST api/Almoxarifado/Emprestimos/{id}/Devolucao"] = new()
        {
            ["disponivel"] = ("Devolução: volta a Disponível", () => new JsonObject
            {
                ["dataDevolucao"] = "2026-10-27", ["conservacaoRetorno"] = "Bom", ["observacoes"] = "Devolvida limpa",
            }),
            ["manutencao"] = ("Devolução com encaminhamento para manutenção", () => new JsonObject
            {
                ["dataDevolucao"] = "2026-10-27", ["conservacaoRetorno"] = "Ruim", ["encaminharParaManutencao"] = true,
                ["motivoManutencao"] = "Vareta quebrada",
            }),
        },
        ["POST api/Almoxarifado/Inventarios"] = new()
        {
            ["fechamento"] = ("Fechamento do inventário anual", () => new JsonObject
            {
                ["dataInventario"] = "2026-10-20", ["responsavelId"] = Membro, ["observacoes"] = "Conferência anual para a Comissão da Igreja",
            }),
        },
        ["POST api/Almoxarifado/Inventarios/{id}/Votacao"] = new()
        {
            ["votacao"] = ("Votação registrada pela Comissão", () => new JsonObject
            {
                ["dataVotacao"] = "2026-11-05", ["referenciaAta"] = "Ata 12/2026, item 4", ["versao"] = Versao,
            }),
        },
        ["POST api/Almoxarifado/Desejos"] = new()
        {
            ["patrimonial"] = ("Cinco barracas (bem patrimonial)", () => new JsonObject
            {
                ["nome"] = "Barraca 4 pessoas", ["descricao"] = "Barracas iglu para acampamentos", ["justificativa"] = "Acampamento de dezembro",
                ["tipoItem"] = "Patrimonial", ["quantidadeDesejada"] = 5, ["urgencia"] = "Urgente", ["categoria"] = "Acampamento",
                ["custoUnitarioEstimado"] = 380.00m, ["dataNecessidade"] = "2026-12-01", ["linkReferencia"] = "https://exemplo.com/barraca",
            }),
            ["consumivel"] = ("Material consumível", () => new JsonObject
            {
                ["nome"] = "Fita crepe", ["descricao"] = "Rolos de fita crepe", ["justificativa"] = "Atividades de unidade", ["tipoItem"] = "Consumivel",
                ["quantidadeDesejada"] = 10, ["urgencia"] = "PoucoUrgenteMasNecessario",
            }),
        },
        ["POST api/Almoxarifado/Desejos/{id}/Atendimentos"] = new()
        {
            ["parcialPatrimonial"] = ("Recebimento parcial de 2 barracas (criadas no atendimento)", () => new JsonObject
            {
                ["quantidade"] = 2, ["data"] = "2026-10-20", ["responsavelId"] = Membro, ["origem"] = "Compra",
                ["bensNovos"] = new JsonArray(Barraca("Barraca 4 pessoas #1"), Barraca("Barraca 4 pessoas #2")),
            }),
            ["consumivel"] = ("Recebimento de consumível (sem bens)", () => new JsonObject
            {
                ["quantidade"] = 10, ["data"] = "2026-10-20", ["responsavelId"] = Membro, ["origem"] = "Doacao", ["observacoes"] = "Doado pela igreja",
            }),
        },
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor action
            || !action.ControllerTypeInfo.IsSubclassOf(typeof(AlmoxarifadoControllerBase)))
        {
            return;
        }

        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        operation.Summary ??= metadata.OfType<IEndpointSummaryMetadata>().FirstOrDefault()?.Summary;
        operation.Description ??= metadata.OfType<IEndpointDescriptionMetadata>().FirstOrDefault()?.Description;

        if (operation.Parameters?.OfType<OpenApiParameter>().FirstOrDefault(p => p.Name == "Idempotency-Key") is { } idempotency)
        {
            idempotency.Required = true;
            idempotency.Description = "UUID deste atendimento (obrigatório). Mesma chave + mesmos dados devolve 200 com o atendimento original, sem " +
                "duplicar recebimentos nem bens; mesma chave com dados diferentes retorna 409; ausente ou inválida retorna 400. Escopo: usuário autenticado.";
        }

        var chave = $"{context.ApiDescription.HttpMethod} {Rota(context.ApiDescription.RelativePath)}";
        if (Exemplos.TryGetValue(chave, out var exemplos)
            && operation.RequestBody?.Content is { } content && content.TryGetValue("application/json", out var json))
        {
            json.Examples = exemplos.ToDictionary(e => e.Key, e => (IOpenApiExample)new OpenApiExample { Summary = e.Value.Resumo, Value = e.Value.Corpo() });
        }
    }

    private static string Rota(string? caminho) => (caminho ?? string.Empty).Replace("{id:guid}", "{id}", StringComparison.Ordinal);

    private static JsonObject Barraca(string nome) => new()
    {
        ["nome"] = nome, ["descricao"] = "Barraca iglu verde", ["categoria"] = "Acampamento", ["estadoConservacao"] = "Novo",
        ["possuiValorComercial"] = true, ["valorEstimado"] = 380.00m, ["localizacao"] = "Almoxarifado - prateleira 2", ["valorAquisicao"] = 380.00m,
    };
}
