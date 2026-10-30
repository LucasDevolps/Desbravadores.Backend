using System.Text.Json;
using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

public sealed class TipoFluxoLancamentoJsonConverter(bool emitLegacyName) : JsonConverter<TipoFluxoLancamento>
{
    public override TipoFluxoLancamento Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined((TipoFluxoLancamento)number))
            return (TipoFluxoLancamento)number;

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString()!.Trim(); // token String: GetString nunca é nulo (só para JsonTokenType.Null)
            if (string.Equals(text, "Entrada", StringComparison.OrdinalIgnoreCase)) return TipoFluxoLancamento.Entrada;
            if (string.Equals(text, "Saida", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "Saída", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "Despesa", StringComparison.OrdinalIgnoreCase)) return TipoFluxoLancamento.Saida;
        }

        throw new JsonException("TipoFluxo inválido. Use 'Entrada' ou 'Saida' (o nome legado 'Despesa' também é aceito).");
    }

    public override void Write(Utf8JsonWriter writer, TipoFluxoLancamento value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == TipoFluxoLancamento.Saida ? (emitLegacyName ? "Despesa" : "Saida") : "Entrada");
}
