using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<TipoFluxoLancamento>))]
public enum TipoFluxoLancamento
{
    [Description("Entrada")]
    Entrada = 0,

    [Description("Saída")]
    Saida = 1,
}
