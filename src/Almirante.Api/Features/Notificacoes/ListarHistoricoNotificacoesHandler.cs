using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Notificacoes;

public sealed class ListarHistoricoNotificacoesHandler(NotificacoesService service)
    : IRequestHandler<ListarHistoricoNotificacoesQuery, HistoricoNotificacoesResponse>
{
    public Task<HistoricoNotificacoesResponse> Handle(ListarHistoricoNotificacoesQuery request, CancellationToken ct) =>
        service.ListarHistoricoAsync(request, ct);
}
