using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class ListLancamentosGeraisHandler(LancamentosGeraisService lancamentosGeraisService)
    : IRequestHandler<ListLancamentosGeraisQuery, LancamentosGeralListResponse>
{
    public Task<LancamentosGeralListResponse> Handle(ListLancamentosGeraisQuery request, CancellationToken cancellationToken) =>
        lancamentosGeraisService.ListAsync(request.Periodo, request.DataInicio, request.DataFim, cancellationToken);
}
