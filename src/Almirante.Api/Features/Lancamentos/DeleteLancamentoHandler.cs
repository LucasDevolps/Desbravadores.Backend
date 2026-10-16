using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;
namespace Almirante.Api.Features.Lancamentos;
public sealed class DeleteLancamentoHandler(LancamentosService service) : IRequestHandler<DeleteLancamentoRequest, bool>
{
    public Task<bool> Handle(DeleteLancamentoRequest r, CancellationToken ct) => service.DeleteAsync(r,ct);
}
