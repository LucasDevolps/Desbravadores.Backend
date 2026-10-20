using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Dashboard;

public sealed class GetDashboardHandler(DashboardService service) : IRequestHandler<GetDashboardQuery, DashboardResponse>
{
    public Task<DashboardResponse> Handle(GetDashboardQuery request, CancellationToken ct) => service.ObterAsync(request, ct);
}
