using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Tesouraria;

public sealed class GetTesourariaResumoHandler(TesourariaService service) : IRequestHandler<GetTesourariaResumoQuery, TesourariaResumoResponse>
{
    public Task<TesourariaResumoResponse> Handle(GetTesourariaResumoQuery request, CancellationToken ct) => service.ObterResumoAsync(ct);
}
