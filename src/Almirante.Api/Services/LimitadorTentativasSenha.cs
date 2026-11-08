using System.Threading.RateLimiting;
using Almirante.Api.Options;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

/// <summary>
/// Limita as tentativas de confirmar senha nas operações de senha. Fica fora do middleware de rate limit porque ele roda antes da autenticação
/// (a chave por usuário só existe depois) e porque a política "login" tem mensagens próprias. Cada requisição consome uma vez, antes de
/// qualquer verificação: retries internos da transação não contam de novo, e falhas do responsável nunca bloqueiam a conta do alvo.
/// </summary>
public sealed class LimitadorTentativasSenha : IDisposable
{
    private readonly PartitionedRateLimiter<(Guid Usuario, string Ip)> _autenticadas;
    private readonly PartitionedRateLimiter<string> _recuperacoes;
    private readonly IDisposable[] _limitadores;

    public LimitadorTentativasSenha(IOptions<SenhasOptions> options)
    {
        var limites = options.Value;
        var janela = TimeSpan.FromMinutes(limites.JanelaMinutos);
        var porUsuario = PartitionedRateLimiter.Create<(Guid Usuario, string Ip), Guid>(chave =>
            RateLimitPartition.GetFixedWindowLimiter(chave.Usuario, _ => Janela(limites.TentativasPorUsuario, janela)));
        var porIp = PartitionedRateLimiter.Create<(Guid Usuario, string Ip), string>(chave =>
            RateLimitPartition.GetFixedWindowLimiter(chave.Ip, _ => Janela(limites.TentativasPorIp, janela)));
        _autenticadas = PartitionedRateLimiter.CreateChained(porUsuario, porIp);
        _recuperacoes = PartitionedRateLimiter.Create<string, string>(ip =>
            RateLimitPartition.GetFixedWindowLimiter(ip, _ => Janela(limites.RecuperacoesPorIp, janela)));
        _limitadores = [_autenticadas, porUsuario, porIp, _recuperacoes];
    }

    /// <summary>Troca de senha ou emissão de autorização por um usuário autenticado: limite por usuário e por IP.</summary>
    public RateLimitLease RegistrarTentativa(Guid usuarioId, string ip) => _autenticadas.AttemptAcquire((usuarioId, ip));

    /// <summary>Pedido ou conclusão de recuperação sem login: limite por IP.</summary>
    public RateLimitLease RegistrarRecuperacao(string ip) => _recuperacoes.AttemptAcquire(ip);

    public void Dispose()
    {
        foreach (var limitador in _limitadores) limitador.Dispose();
    }

    private static FixedWindowRateLimiterOptions Janela(int permitidas, TimeSpan janela) => new()
    {
        PermitLimit = permitidas,
        Window = janela,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
