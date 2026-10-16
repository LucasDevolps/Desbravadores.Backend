using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<CategoriaLancamento>))]
public enum CategoriaLancamento
{
    [Description("Evento")]
    Evento = 0,

    [Description("Clube")]
    Clube = 1,
}
