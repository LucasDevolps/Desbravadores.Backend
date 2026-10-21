using System.Collections.Concurrent;
using System.Security.Claims;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Almirante.Api.Hubs;

/// <summary>
/// Hub só de saída: o servidor avisa o próprio usuário; o cliente não tem métodos para chamar, não escolhe destinatário e não
/// entra em grupos. O roteamento é por <see cref="UsuarioIdProvider"/> (Usuario.Id do token validado). Os avisos são sinais
/// de sincronização; a fonte da verdade é GET /api/Notificacoes/Pendentes.
/// </summary>
[Authorize]
public sealed class NotificacoesHub(ConexoesNotificacoes conexoes, ILogger<NotificacoesHub> logger) : Hub
{
    public const string Caminho = "/hubs/notificacoes";

    public const string EventoNotificacaoCriada = "NotificacaoCriada";
    public const string EventoNotificacaoLida = "NotificacaoLida";

    public override Task OnConnectedAsync()
    {
        var usuario = Context.User;
        if (usuario is null || !usuario.TentarObterUsuarioId(out var usuarioId)
            || !Guid.TryParse(usuario.FindFirstValue("sid"), out var sessaoId)
            || usuario.FindFirstValue("role") is not { Length: > 0 } role)
        {
            Context.Abort();
            return Task.CompletedTask;
        }

        conexoes.Registrar(new ConexaoNotificacoes(Context.ConnectionId, usuarioId, sessaoId, role, Context.Abort));
        logger.LogDebug("Conexão {ConnectionId} do hub de notificações aberta para {UsuarioId}.", Context.ConnectionId, usuarioId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        conexoes.Remover(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Usuário do SignalR = Usuario.Id (claim sub validada pelo JwtBearer e pela sessão), sempre no formato "D".</summary>
public sealed class UsuarioIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User.TentarObterUsuarioId(out var usuarioId) ? Formatar(usuarioId) : null;

    public static string Formatar(Guid usuarioId) => usuarioId.ToString("D");
}

public sealed record ConexaoNotificacoes(string ConnectionId, Guid UsuarioId, Guid SessaoId, string Role, Action Encerrar);

/// <summary>Conexões abertas nesta instância, para revalidar as sessões enquanto a conexão dura.</summary>
public sealed class ConexoesNotificacoes
{
    private readonly ConcurrentDictionary<string, ConexaoNotificacoes> _conexoes = new(StringComparer.Ordinal);

    public void Registrar(ConexaoNotificacoes conexao) => _conexoes[conexao.ConnectionId] = conexao;

    public void Remover(string connectionId) => _conexoes.TryRemove(connectionId, out _);

    public IReadOnlyCollection<ConexaoNotificacoes> Abertas() => _conexoes.Values.ToArray();
}
