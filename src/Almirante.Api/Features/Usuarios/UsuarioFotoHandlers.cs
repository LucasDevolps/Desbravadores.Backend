using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Usuarios;

public sealed class EnviarFotoUsuarioHandler(UsuarioFotoService service) : IRequestHandler<EnviarFotoUsuarioRequest, UsuarioListItemDto>
{
    public Task<UsuarioListItemDto> Handle(EnviarFotoUsuarioRequest request, CancellationToken ct) => service.EnviarAsync(request, ct);
}

public sealed class ObterFotoUsuarioHandler(UsuarioFotoService service) : IRequestHandler<ObterFotoUsuarioQuery, FotoUsuarioArquivo?>
{
    public Task<FotoUsuarioArquivo?> Handle(ObterFotoUsuarioQuery request, CancellationToken ct) => service.ObterAsync(request.Id, ct);
}

public sealed class ExcluirFotoUsuarioHandler(UsuarioFotoService service) : IRequestHandler<ExcluirFotoUsuarioRequest, Unit>
{
    public async Task<Unit> Handle(ExcluirFotoUsuarioRequest request, CancellationToken ct)
    {
        await service.ExcluirAsync(request, ct);
        return Unit.Value;
    }
}
