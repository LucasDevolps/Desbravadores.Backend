using System.Text.Json.Serialization;

namespace Almirante.Api.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter<FiltroLeituraNotificacoes>))]
public enum FiltroLeituraNotificacoes
{
    Todas = 0,
    Lidas = 1,
    NaoLidas = 2,
}
