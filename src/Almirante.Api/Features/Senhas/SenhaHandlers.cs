using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Senhas;

public sealed class AlterarPropriaSenhaHandler(AlteracaoSenhaService service) : IRequestHandler<AlterarPropriaSenhaRequest>
{
    public Task Handle(AlterarPropriaSenhaRequest request, CancellationToken ct) => service.AlterarPropriaAsync(request, ct);
}

public sealed class AlterarSenhaUsuarioHandler(AlteracaoSenhaService service) : IRequestHandler<AlterarSenhaUsuarioRequest>
{
    public Task Handle(AlterarSenhaUsuarioRequest request, CancellationToken ct) => service.AlterarDeUsuarioAsync(request, ct);
}

public sealed class ConfirmarRecuperacaoSenhaHandler(AlteracaoSenhaService service) : IRequestHandler<ConfirmarRecuperacaoSenhaRequest>
{
    public Task Handle(ConfirmarRecuperacaoSenhaRequest request, CancellationToken ct) => service.ConcluirRecuperacaoAsync(request, ct);
}

public sealed class SolicitarRecuperacaoSenhaHandler(RecuperacaoSenhaService service) : IRequestHandler<SolicitarRecuperacaoSenhaRequest>
{
    public Task Handle(SolicitarRecuperacaoSenhaRequest request, CancellationToken ct) => service.SolicitarAsync(request, ct);
}

public sealed class ListarRecuperacoesSenhaPendentesHandler(RecuperacaoSenhaService service)
    : IRequestHandler<ListarRecuperacoesSenhaPendentesQuery, RecuperacoesSenhaPendentesResponse>
{
    public Task<RecuperacoesSenhaPendentesResponse> Handle(ListarRecuperacoesSenhaPendentesQuery request, CancellationToken ct) =>
        service.ListarPendentesAsync(request, ct);
}

public sealed class EmitirAutorizacaoRecuperacaoHandler(RecuperacaoSenhaService service)
    : IRequestHandler<EmitirAutorizacaoRecuperacaoRequest, AutorizacaoRecuperacaoSenhaResponse>
{
    public Task<AutorizacaoRecuperacaoSenhaResponse> Handle(EmitirAutorizacaoRecuperacaoRequest request, CancellationToken ct) =>
        service.EmitirAutorizacaoAsync(request, ct);
}
