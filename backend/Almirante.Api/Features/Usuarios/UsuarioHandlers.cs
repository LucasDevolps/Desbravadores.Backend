using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Usuarios;

public sealed class RegistrarUsuarioHandler(UsuariosService service) : IRequestHandler<RegistrarUsuarioRequest, UsuarioListItemDto>
{
    public Task<UsuarioListItemDto> Handle(RegistrarUsuarioRequest request, CancellationToken ct) => service.RegistrarAsync(request, ct);
}

public sealed class UpdateUsuarioHandler(UsuariosService service) : IRequestHandler<UpdateUsuarioRequest, UsuarioListItemDto>
{
    public Task<UsuarioListItemDto> Handle(UpdateUsuarioRequest request, CancellationToken ct) => service.UpdateAsync(request, ct);
}

public sealed class DeleteUsuarioHandler(UsuariosService service) : IRequestHandler<DeleteUsuarioRequest, Unit>
{
    public async Task<Unit> Handle(DeleteUsuarioRequest request, CancellationToken ct)
    {
        await service.DeleteAsync(request, ct);
        return Unit.Value;
    }
}
