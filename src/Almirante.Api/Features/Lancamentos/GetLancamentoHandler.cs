using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class GetLancamentoHandler(LancamentosService service) : IRequestHandler<GetLancamentoQuery, LancamentoDto?>
{
    public Task<LancamentoDto?> Handle(GetLancamentoQuery request, CancellationToken ct) =>
        service.GetByIdAsync(request.Id, ct);
}
