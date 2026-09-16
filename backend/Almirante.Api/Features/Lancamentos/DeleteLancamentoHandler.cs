using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class DeleteLancamentoHandler(LancamentosService lancamentosService) : IRequestHandler<DeleteLancamentoCommand, bool>
{
    public Task<bool> Handle(DeleteLancamentoCommand request, CancellationToken cancellationToken) =>
        lancamentosService.DeleteAsync(request.Id, cancellationToken);
}
