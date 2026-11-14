using MediatR;

namespace Almirante.Api.Dtos;

public sealed record ListarHistoricoNotificacoesQuery(Guid UsuarioId, FiltroLeituraNotificacoes Filtro, int PageSize, string? Cursor)
    : IRequest<HistoricoNotificacoesResponse>;
