using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class CreateLancamentoGeralHandler(LancamentosGeraisService lancamentosGeraisService)
    : IRequestHandler<CreateLancamentoGeralRequest, LancamentoGeralCreateResult>
{
    public Task<LancamentoGeralCreateResult> Handle(CreateLancamentoGeralRequest request, CancellationToken cancellationToken) =>
        lancamentosGeraisService.CreateAsync(request.IdempotencyKey, request, request.UsuarioSolicitanteId, cancellationToken);
}
