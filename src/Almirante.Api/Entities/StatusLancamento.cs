using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<StatusLancamento>))]
public enum StatusLancamento
{
    [Description("Pendente")]
    Pendente = 0,

    [Description("Pago")]
    Pago = 1,

    [Description("Atrasado")]
    Atrasado = 2,
}
