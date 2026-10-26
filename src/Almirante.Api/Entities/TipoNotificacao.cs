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

    /// <summary>Senha alterada por outra pessoa (troca administrativa ou recuperação assistida); nunca na troca própria.</summary>
    [Description("Senha alterada")]
    SenhaAlterada = 3,
}

/// <summary>Aviso em tempo real ainda não emitido para o destinatário.</summary>
public enum AvisoNotificacao
{
    Criada = 1,
    Lida = 2,
}
