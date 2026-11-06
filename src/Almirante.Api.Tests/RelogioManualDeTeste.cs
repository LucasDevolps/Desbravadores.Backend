using System.Collections.Concurrent;

namespace Almirante.Api.Tests;

/// <summary>
/// Relógio controlado pelo teste: <c>Task.Delay(prazo, relogio, token)</c> só termina quando o teste chama <see cref="Disparar"/>,
/// então um laço de serviço em segundo plano roda uma iteração por vez, sem espera real nem dependência de horário.
/// </summary>
public sealed class RelogioManualDeTeste(DateTimeOffset? agora = null) : TimeProvider
{
    private readonly ConcurrentQueue<(TimerCallback Callback, object? Estado, TimeSpan Prazo)> _pendentes = new();

    public DateTimeOffset Agora { get; set; } = agora ?? DateTimeOffset.UtcNow;

    public int Pendentes => _pendentes.Count;

    public override DateTimeOffset GetUtcNow() => Agora;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _pendentes.Enqueue((callback, state, dueTime));
        return new TimerManual();
    }

    /// <summary>Espera o serviço chegar ao próximo <c>Task.Delay</c> e devolve o prazo pedido, sem dispará-lo.</summary>
    public async Task<TimeSpan> AguardarTimerAsync(TimeSpan? limite = null)
    {
        var fim = DateTime.UtcNow + (limite ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < fim)
        {
            if (_pendentes.TryPeek(out var timer)) return timer.Prazo;
            await Task.Delay(10);
        }
        throw new TimeoutException("O serviço não chegou ao Task.Delay dentro do prazo.");
    }

    /// <summary>Faz o <c>Task.Delay</c> pendente mais antigo terminar e devolve o prazo que ele tinha.</summary>
    public TimeSpan Disparar()
    {
        Assert.True(_pendentes.TryDequeue(out var timer), "Nenhum Task.Delay pendente para disparar.");
        timer.Callback(timer.Estado);
        return timer.Prazo;
    }

    private sealed class TimerManual : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
