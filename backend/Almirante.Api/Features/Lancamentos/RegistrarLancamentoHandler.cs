using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;
namespace Almirante.Api.Features.Lancamentos;
public sealed class RegistrarLancamentoHandler(LancamentosService service) : IRequestHandler<RegistrarLancamentoRequest, RegistrarLancamentoResult>
{
    public Task<RegistrarLancamentoResult> Handle(RegistrarLancamentoRequest request, CancellationToken ct) => service.RegistrarAsync(request, ct);
}
