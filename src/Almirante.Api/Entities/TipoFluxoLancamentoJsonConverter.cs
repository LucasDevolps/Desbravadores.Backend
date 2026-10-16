using System.Text.Json;
using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

// Janela de compatibilidade da transição "Despesa" -> "Saida" (enum tipado da issue #50).
//   - Entrada (request): aceita "Entrada", "Saida", "Saída", o nome legado "Despesa" (sem diferenciar
//     maiúsculas/minúsculas) e o valor numérico 0/1 — clientes antigos continuam enviando "Despesa".
//   - Saída (response): "Saida" por padrão. Enquanto houver cliente antigo que LÊ "Despesa" na resposta,
//     ligue Compatibility:LegacyTipoFluxoDespesa=true; desligue quando todos os clientes usarem "Saida".
// Registrado em JsonOptions (Program.cs), que tem precedência sobre o [JsonConverter] do enum.
public sealed class TipoFluxoLancamentoJsonConverter(bool emitLegacyName) : JsonConverter<TipoFluxoLancamento>
{
    public override TipoFluxoLancamento Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined((TipoFluxoLancamento)number))
            return (TipoFluxoLancamento)number;

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString()?.Trim();
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
