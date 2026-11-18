using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

public sealed class RegistrarPagamentoHandler(LancamentosService service) : IRequestHandler<RegistrarPagamentoRequest, LancamentoDto?>
{
    public Task<LancamentoDto?> Handle(RegistrarPagamentoRequest request, CancellationToken ct) => service.RegistrarPagamentoAsync(request, ct);
}

public sealed class AtualizarPagamentoHandler(LancamentosService service) : IRequestHandler<AtualizarPagamentoRequest, LancamentoDto?>
{
    public Task<LancamentoDto?> Handle(AtualizarPagamentoRequest request, CancellationToken ct) => service.AtualizarPagamentoAsync(request, ct);
}

public sealed class RegistrarDevolucaoHandler(LancamentosService service) : IRequestHandler<RegistrarDevolucaoRequest, LancamentoDto?>
{
    public Task<LancamentoDto?> Handle(RegistrarDevolucaoRequest request, CancellationToken ct) => service.RegistrarDevolucaoAsync(request, ct);
}

public sealed class AtualizarDevolucaoHandler(LancamentosService service) : IRequestHandler<AtualizarDevolucaoRequest, LancamentoDto?>
{
    public Task<LancamentoDto?> Handle(AtualizarDevolucaoRequest request, CancellationToken ct) => service.AtualizarDevolucaoAsync(request, ct);
}
