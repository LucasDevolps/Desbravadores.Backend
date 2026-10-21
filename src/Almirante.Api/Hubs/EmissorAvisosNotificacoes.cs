using Microsoft.AspNetCore.SignalR;

namespace Almirante.Api.Hubs;

/// <summary>Entrega de um aviso às conexões do destinatário. Concluir não significa que alguém recebeu ou leu.</summary>
public interface IEmissorAvisosNotificacoes
{
    Task EmitirAsync(Guid destinatarioId, string evento, object payload, CancellationToken ct);
}

public sealed class EmissorAvisosHub(IHubContext<NotificacoesHub> hub) : IEmissorAvisosNotificacoes
{
    public Task EmitirAsync(Guid destinatarioId, string evento, object payload, CancellationToken ct) =>
        hub.Clients.User(UsuarioIdProvider.Formatar(destinatarioId)).SendAsync(evento, payload, ct);
}
