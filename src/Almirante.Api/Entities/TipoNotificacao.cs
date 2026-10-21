using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Almirante.Api.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<TipoNotificacao>))]
public enum TipoNotificacao
{
    [Description("Débito cadastrado")]
    DebitoCadastrado = 1,

    [Description("Evento cadastrado")]
    EventoCadastrado = 2,
}

/// <summary>Aviso em tempo real ainda não emitido para o destinatário.</summary>
public enum AvisoNotificacao
{
    Criada = 1,
    Lida = 2,
}
