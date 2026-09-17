using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class ListLancamentosHandler(LancamentosService lancamentosService) : IRequestHandler<ListLancamentosQuery, LancamentosResponse>
{
    public Task<LancamentosResponse> Handle(ListLancamentosQuery request, CancellationToken cancellationToken) =>
        lancamentosService.ListAsync(request.Page, request.PageSize, request.Search, request.Status, request.Tipo, request.Data, cancellationToken);
}
