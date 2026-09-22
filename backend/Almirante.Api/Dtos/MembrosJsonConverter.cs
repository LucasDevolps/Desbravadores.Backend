using System.Text.Json;
using System.Text.Json.Serialization;

namespace Almirante.Api.Dtos;

// Contrato do campo "membros" (POST e PUT /api/Eventos): aceita um GUID único OU um array de GUIDs.
//   "membros": "11111111-1111-4111-8111-111111111111"
//   "membros": ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"]
// Os dois formatos são normalizados AQUI, na desserialização, para IReadOnlyList<Guid>; a partir daí toda a
// aplicação (validação, hash de idempotência, serviço) enxerga uma única representação. O conversor NÃO
// remove duplicidades nem descarta GUIDs vazios: quem valida é o FluentValidator (erro por campo).
// Rejeita null, números, objetos, booleanos e strings que não sejam GUID (JsonException -> 400).
public sealed class MembrosJsonConverter : JsonConverter<IReadOnlyList<Guid>>
{
    // Limite defensivo: o array é lido inteiro na memória e depois vira parâmetro de consulta ao banco.
    public const int MaximoMembros = 1000;

    // null chega ao Read (em vez de virar null silenciosamente) para poder ser recusado explicitamente.
    public override bool HandleNull => true;

    public override IReadOnlyList<Guid> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return [LerGuid(ref reader)];

            case JsonTokenType.StartArray:
                var membros = new List<Guid>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("membros deve conter apenas GUIDs (strings).");
                    }

                    if (membros.Count >= MaximoMembros)
                    {
                        throw new JsonException($"membros aceita no máximo {MaximoMembros} itens.");
                    }

                    membros.Add(LerGuid(ref reader));
                }

                return membros;

            default:
                throw new JsonException("membros deve ser um GUID ou um array de GUIDs (não pode ser null nem outro tipo JSON).");
        }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<Guid> value, JsonSerializerOptions options)
    {
        // Saída previsível: sempre array, mesmo que a entrada tenha sido um GUID único.
        writer.WriteStartArray();
        foreach (var id in value)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    private static Guid LerGuid(ref Utf8JsonReader reader) =>
        reader.TryGetGuid(out var id) ? id : throw new JsonException("membros contém um GUID inválido.");
}
