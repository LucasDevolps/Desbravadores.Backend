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

    /// <summary>Nula enquanto a notificação está pendente.</summary>
    public DateTime? LidaEmUtc { get; init; }

    /// <summary>
    /// Metadados do registro de origem; não concedem acesso por si: o destinatário consulta o evento em GET /api/Me/Eventos/{eventoId}
    /// (só enquanto participa dele); /api/Eventos e /api/Lancamentos seguem as policies de gestão.
    /// </summary>
    public Guid? EventoId { get; init; }
    public Guid? LancamentoId { get; init; }
}

public sealed record NotificacoesPendentesResponse
{
    /// <summary>Da mais recente para a mais antiga (criadaEmUtc, depois id).</summary>
    public required IReadOnlyList<NotificacaoDto> Items { get; init; }

    /// <summary>Total de notificações não lidas do usuário, independente da página.</summary>
    public int NaoLidas { get; init; }

    public int PageSize { get; init; }

    /// <summary>Cursor opaco da próxima página; nulo na última.</summary>
    public string? ProximoCursor { get; init; }
}

public sealed record LeituraNotificacaoResponse(NotificacaoDto Notificacao, int NaoLidas);

public sealed record ListarNotificacoesPendentesQuery(Guid UsuarioId, int PageSize, string? Cursor) : IRequest<NotificacoesPendentesResponse>;

public sealed record MarcarNotificacaoLidaCommand(Guid UsuarioId, Guid Id) : IRequest<LeituraNotificacaoResponse?>;

/// <summary>Payload do evento NotificacaoCriada do hub.</summary>
public sealed record NotificacaoCriadaAviso(NotificacaoDto Notificacao, int NaoLidas);

/// <summary>Payload do evento NotificacaoLida do hub.</summary>
public sealed record NotificacaoLidaAviso(Guid NotificacaoId, DateTime LidaEmUtc, int NaoLidas);
