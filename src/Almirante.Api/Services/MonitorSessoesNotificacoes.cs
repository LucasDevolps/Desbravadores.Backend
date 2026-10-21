using Almirante.Api.Data;
using Almirante.Api.Hubs;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

/// <summary>
/// A validação do token só acontece ao conectar. Este serviço revalida periodicamente as sessões das conexões abertas e
/// encerra as que foram revogadas (logout, reuso de refresh, cargo inativo, usuário desativado). A expiração do próprio
/// token é tratada pelo SignalR (CloseOnAuthenticationExpiration).
/// </summary>
public sealed class MonitorSessoesNotificacoes(ConexoesNotificacoes conexoes, IServiceScopeFactory scopes, TimeProvider clock,
    IOptions<NotificacoesOptions> options, ILogger<MonitorSessoesNotificacoes> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMilliseconds(options.Value.VerificacaoSessoesMs);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalo, clock, stoppingToken);
                await VerificarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Falha ao revalidar as sessões das conexões do hub de notificações."); }
        }
    }

    public async Task<int> VerificarAsync(CancellationToken ct)
    {
        var abertas = conexoes.Abertas();
        if (abertas.Count == 0)
        {
            return 0;
        }

        var sessoes = abertas.Select(c => c.SessaoId).Distinct().ToList();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var agora = clock.GetUtcNow().UtcDateTime;
        var vigentes = (await db.AuthSessions.AsNoTracking()
                .Where(SessoesAutenticacao.Vigente(agora))
                .Where(s => sessoes.Contains(s.Id))
                .Select(s => new { s.Id, s.UsuarioId, Role = s.Usuario!.Cargo!.Role })
                .ToListAsync(ct))
            .Select(s => (s.Id, s.UsuarioId, s.Role))
            .ToHashSet();

        var encerradas = 0;
        foreach (var conexao in abertas.Where(c => !vigentes.Contains((c.SessaoId, c.UsuarioId, c.Role))))
        {
            logger.LogInformation("Conexão {ConnectionId} do hub de notificações encerrada: sessão de {UsuarioId} não está mais vigente.",
                conexao.ConnectionId, conexao.UsuarioId);
            conexoes.Remover(conexao.ConnectionId);
            conexao.Encerrar();
            encerradas++;
        }

        return encerradas;
    }
}
