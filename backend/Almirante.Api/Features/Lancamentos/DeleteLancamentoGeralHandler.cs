using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class DeleteLancamentoGeralHandler(LancamentosGeraisService lancamentosGeraisService)
    : IRequestHandler<DeleteLancamentoGeralRequest, LancamentoGeralDeleteOutcome>
{
    public Task<LancamentoGeralDeleteOutcome> Handle(DeleteLancamentoGeralRequest request, CancellationToken cancellationToken) =>
        lancamentosGeraisService.DeleteAsync(
            request.Id,
            request.Motivo.Trim(),
            request.UsuarioResponsavelId,
            request.IpResponsavel,
            cancellationToken);
}
