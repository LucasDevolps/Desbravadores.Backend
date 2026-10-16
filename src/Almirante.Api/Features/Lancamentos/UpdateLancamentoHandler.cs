using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class UpdateLancamentoHandler(LancamentosService lancamentosService) : IRequestHandler<UpdateLancamentoRequest, LancamentoDto?>
{
    public Task<LancamentoDto?> Handle(UpdateLancamentoRequest request, CancellationToken cancellationToken) =>
        lancamentosService.UpdateAsync(request.Id, request, cancellationToken);
}
