using Almirante.Api.Entities;
using MediatR;

namespace Almirante.Api.Dtos;

public sealed record NotificacaoDto
{
    public Guid Id { get; init; }
    public TipoNotificacao Tipo { get; init; }
    public required string Titulo { get; init; }
    public required string Mensagem { get; init; }
    public DateTime CriadaEmUtc { get; init; }

    public DateTime? LidaEmUtc { get; init; }

    public Guid? EventoId { get; init; }
    public Guid? LancamentoId { get; init; }
}

public sealed record NotificacoesPendentesResponse
{
    public required IReadOnlyList<NotificacaoDto> Items { get; init; }

    public int NaoLidas { get; init; }

    public int PageSize { get; init; }

    public string? ProximoCursor { get; init; }
}

public sealed record LeituraNotificacaoResponse(NotificacaoDto Notificacao, int NaoLidas);

/// <param name="Marcadas">Quantas pendências esta chamada marcou como lidas (0 quando não havia nenhuma até o limite).</param>
/// <param name="NaoLidas">Total de não lidas depois da gravação: as criadas depois do limite continuam pendentes.</param>
/// <param name="LimiteUtc">Instante em que a ação começou: entram as criadas até ele, e as marcadas recebem essa data em lidaEmUtc.</param>
public sealed record LeituraTodasNotificacoesResponse(int Marcadas, int NaoLidas, DateTime LimiteUtc);

public sealed record ListarNotificacoesPendentesQuery(Guid UsuarioId, int PageSize, string? Cursor) : IRequest<NotificacoesPendentesResponse>;

public sealed record MarcarNotificacaoLidaCommand(Guid UsuarioId, Guid Id) : IRequest<LeituraNotificacaoResponse?>;

public sealed record MarcarTodasNotificacoesLidasCommand(Guid UsuarioId) : IRequest<LeituraTodasNotificacoesResponse>;

public sealed record NotificacaoCriadaAviso(NotificacaoDto Notificacao, int NaoLidas);

public sealed record NotificacaoLidaAviso(Guid NotificacaoId, DateTime LidaEmUtc, int NaoLidas);
