using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Eventos;

public sealed class ListEventosHandler(EventosService service) : IRequestHandler<ListEventosQuery, EventosResponse>
{
    public Task<EventosResponse> Handle(ListEventosQuery request, CancellationToken ct) =>
        service.ListAsync(request.DataInicial, request.DataFinal, ct);
}

public sealed class GetEventoHandler(EventosService service) : IRequestHandler<GetEventoQuery, EventoDto?>
{
    public Task<EventoDto?> Handle(GetEventoQuery request, CancellationToken ct) => service.GetByIdAsync(request.Id, ct);
}

public sealed class RegistrarEventoHandler(EventosService service) : IRequestHandler<RegistrarEventoRequest, RegistrarEventoResult>
{
    public Task<RegistrarEventoResult> Handle(RegistrarEventoRequest request, CancellationToken ct) => service.RegistrarAsync(request, ct);
}

public sealed class UpdateEventoHandler(EventosService service) : IRequestHandler<UpdateEventoRequest, EventoDto>
{
    public Task<EventoDto> Handle(UpdateEventoRequest request, CancellationToken ct) => service.UpdateAsync(request, ct);
}

public sealed class DeleteEventoHandler(EventosService service) : IRequestHandler<DeleteEventoRequest, Unit>
{
    public async Task<Unit> Handle(DeleteEventoRequest request, CancellationToken ct)
    {
        await service.DeleteAsync(request, ct);
        return Unit.Value;
    }
}
