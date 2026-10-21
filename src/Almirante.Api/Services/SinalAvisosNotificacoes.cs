using System.Threading.Channels;

namespace Almirante.Api.Services;

/// <summary>
/// Acorda o publicador de avisos logo depois de um commit que gravou aviso pendente. É só um atalho de latência: perder o
/// sinal (reinício, outra instância) não perde aviso, porque a varredura periódica lê as pendências do banco.
/// </summary>
public sealed class SinalAvisosNotificacoes
{
    private readonly Channel<bool> _canal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Sinalizar() => _canal.Writer.TryWrite(true);

    /// <summary>Espera um sinal ou o fim do prazo, o que vier primeiro.</summary>
    public async Task AguardarAsync(TimeSpan prazo, TimeProvider clock, CancellationToken ct)
    {
        using var limite = new CancellationTokenSource(prazo, clock);
        using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct, limite.Token);
        try
        {
            await _canal.Reader.WaitToReadAsync(espera.Token);
            _canal.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}
