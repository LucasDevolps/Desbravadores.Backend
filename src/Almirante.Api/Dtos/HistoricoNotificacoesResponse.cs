namespace Almirante.Api.Dtos;

public sealed record HistoricoNotificacoesResponse
{
    public required IReadOnlyList<NotificacaoDto> Items { get; init; }

    public FiltroLeituraNotificacoes Filtro { get; init; }

    public int NaoLidas { get; init; }

    public int PageSize { get; init; }

    public string? ProximoCursor { get; init; }
}
