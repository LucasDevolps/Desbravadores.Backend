using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Notificacoes;

public sealed class ListarNotificacoesPendentesHandler(NotificacoesService service)
    : IRequestHandler<ListarNotificacoesPendentesQuery, NotificacoesPendentesResponse>
{
    public Task<NotificacoesPendentesResponse> Handle(ListarNotificacoesPendentesQuery request, CancellationToken ct) =>
        service.ListarPendentesAsync(request, ct);
}

public sealed class MarcarNotificacaoLidaHandler(NotificacoesService service)
    : IRequestHandler<MarcarNotificacaoLidaCommand, LeituraNotificacaoResponse?>
{
    public Task<LeituraNotificacaoResponse?> Handle(MarcarNotificacaoLidaCommand request, CancellationToken ct) =>
        service.MarcarComoLidaAsync(request, ct);
}
