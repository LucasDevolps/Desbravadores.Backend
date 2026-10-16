using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;
namespace Almirante.Api.Features.Lancamentos;
public sealed class ListLancamentosHandler(LancamentosService service) : IRequestHandler<ListLancamentosQuery, LancamentosResponse>
{
    public Task<LancamentosResponse> Handle(ListLancamentosQuery r, CancellationToken ct) => service.ListAsync(r.Page,r.PageSize,r.Search,r.Status,r.Finalidade,r.Vencimento,ct);
}
