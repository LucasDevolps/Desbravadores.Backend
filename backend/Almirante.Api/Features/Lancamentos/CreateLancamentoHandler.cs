using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class CreateLancamentoHandler(LancamentosService lancamentosService) : IRequestHandler<CreateLancamentoRequest, LancamentoDto>
{
    public Task<LancamentoDto> Handle(CreateLancamentoRequest request, CancellationToken cancellationToken) =>
        lancamentosService.CreateAsync(request, cancellationToken);
}
