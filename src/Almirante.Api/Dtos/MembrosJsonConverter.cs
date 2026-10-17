using System.Text.Json;
using System.Text.Json.Serialization;

namespace Almirante.Api.Dtos;

public sealed class MembrosJsonConverter : JsonConverter<IReadOnlyList<Guid>>
{
    public const int MaximoMembros = 1000;

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
