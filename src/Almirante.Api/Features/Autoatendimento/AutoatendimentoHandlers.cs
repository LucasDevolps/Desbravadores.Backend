using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Autoatendimento;

public sealed class ObterMeusDadosHandler(UsuariosService service) : IRequestHandler<ObterMeusDadosQuery, MeusDadosDto>
{
    public async Task<MeusDadosDto> Handle(ObterMeusDadosQuery request, CancellationToken ct) =>
        await service.ObterMeusDadosAsync(request.UsuarioId, ct) ?? throw ApiProblemException.NotFound("Usuário não encontrado.");
}

public sealed class AtualizarMeusDadosHandler(UsuariosService service) : IRequestHandler<AtualizarMeusDadosRequest, MeusDadosDto>
{
    public Task<MeusDadosDto> Handle(AtualizarMeusDadosRequest request, CancellationToken ct) => service.AtualizarMeusDadosAsync(request, ct);
}

public sealed class ListarMeusEventosHandler(EventosService service) : IRequestHandler<ListarMeusEventosQuery, MeusEventosResponse>
{
    public async Task<MeusEventosResponse> Handle(ListarMeusEventosQuery request, CancellationToken ct) =>
        new() { Items = await service.ListarDoParticipanteAsync(request.UsuarioId, null, ct) };
}

public sealed class ObterMeuEventoHandler(EventosService service) : IRequestHandler<ObterMeuEventoQuery, EventoParticipanteDto?>
{
    public async Task<EventoParticipanteDto?> Handle(ObterMeuEventoQuery request, CancellationToken ct) =>
        (await service.ListarDoParticipanteAsync(request.UsuarioId, request.EventoId, ct)).SingleOrDefault();
}

public sealed class ListarMembrosHandler(UsuariosService service) : IRequestHandler<ListarMembrosQuery, IReadOnlyList<MembroResumoDto>>
{
    public Task<IReadOnlyList<MembroResumoDto>> Handle(ListarMembrosQuery request, CancellationToken ct) => service.ListarMembrosAsync(ct);
}
