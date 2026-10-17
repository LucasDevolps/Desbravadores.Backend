using System.Collections.Concurrent;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Almirante.Api.Tests;

public sealed class FotosCleanupServiceTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private sealed class LogEmLista : ILogger<FotosCleanupService>
    {
        public ConcurrentQueue<string> Entradas { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }

    private sealed class ArmazenamentoDeTeste(Func<CancellationToken, Task<int>> purge) : IFileStorage
    {
        public Task SaveAsync(string chave, ReadOnlyMemory<byte> conteudo, CancellationToken ct) => Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string chave, CancellationToken ct) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string chave, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ArquivoArmazenado>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ArquivoArmazenado>>([]);
        public Task<int> PurgeIncompleteAsync(DateTime anterioresAUtc, CancellationToken ct) => purge(ct);
    }

    private sealed class RelogioManual : TimeProvider
    {
        private readonly ConcurrentQueue<(TimerCallback Callback, object? Estado, TimeSpan Prazo)> pendentes = new();

        public int Pendentes => pendentes.Count;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            pendentes.Enqueue((callback, state, dueTime));
            return new TimerManual();
        }

        public TimeSpan Disparar()
        {
            Assert.True(pendentes.TryDequeue(out var timer));
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

    private FotosCleanupService Servico(IFileStorage armazenamento, LogEmLista log, bool habilitada = true, TimeProvider? relogio = null) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), armazenamento,
            Microsoft.Extensions.Options.Options.Create(new MediaOptions { Storage = { Path = factory.FotosPath, LimpezaHabilitada = habilitada } }),
            relogio ?? TimeProvider.System, log);

    private string Caminho(string chave) => Path.Combine(factory.FotosPath, chave.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Limpar_RemoveSoOrfasAntigasETemporariosAntigos_EMantemOResto()
    {
        var armazenamento = factory.Services.GetRequiredService<IFileStorage>();
        var dono = await TestHelpers.AddUsuarioAsync(factory, "Dono da foto", "DS");
        var referenciada = FotoChave.Gerar(dono.Id);
        var orfaAntiga = FotoChave.Gerar(Guid.NewGuid());
        var orfaRecente = FotoChave.Gerar(Guid.NewGuid());
        foreach (var chave in new[] { referenciada, orfaAntiga, orfaRecente })
        {
            await armazenamento.SaveAsync(chave, new byte[] { 1 }, CancellationToken.None);
        }

        await TestHelpers.WithDbAsync(factory, async db =>
        {
            (await db.Usuarios.SingleAsync(u => u.Id == dono.Id)).FotoCaminho = referenciada;
            return await db.SaveChangesAsync();
        });
        File.SetLastWriteTimeUtc(Caminho(referenciada), DateTime.UtcNow.AddHours(-30));
        File.SetLastWriteTimeUtc(Caminho(orfaAntiga), DateTime.UtcNow.AddHours(-30));
        var pasta = Path.GetDirectoryName(Caminho(referenciada))!;
        var tmpAntigo = Path.Combine(pasta, ".tmp-interrompido");
        var tmpRecente = Path.Combine(pasta, ".tmp-em-andamento");
        await File.WriteAllBytesAsync(tmpAntigo, [1]);
        await File.WriteAllBytesAsync(tmpRecente, [1]);
        File.SetLastWriteTimeUtc(tmpAntigo, DateTime.UtcNow.AddHours(-2));
        var log = new LogEmLista();

        var (incompletos, orfas) = await Servico(armazenamento, log).LimparAsync(CancellationToken.None);

        Assert.Equal((1, 1), (incompletos, orfas));
        Assert.True(File.Exists(Caminho(referenciada)));
        Assert.False(File.Exists(Caminho(orfaAntiga)));
        Assert.True(File.Exists(Caminho(orfaRecente)));
        Assert.False(File.Exists(tmpAntigo));
        Assert.True(File.Exists(tmpRecente));
        Assert.Contains(log.Entradas, e => e.StartsWith("Information") && e.Contains("1 foto(s) órfã(s)"));
    }

    [Fact]
    public async Task Limpar_SemNadaParaRemover_NaoConsultaOBancoNemLoga()
    {
        var log = new LogEmLista();

        var resultado = await Servico(new ArmazenamentoDeTeste(_ => Task.FromResult(0)), log).LimparAsync(CancellationToken.None);

        Assert.Equal((0, 0), resultado);
        Assert.Empty(log.Entradas);
    }

    [Fact]
    public async Task Laco_Desabilitado_NaoFazNada()
    {
        var chamadas = 0;
        var servico = Servico(new ArmazenamentoDeTeste(_ => Task.FromResult(++chamadas)), new LogEmLista(), habilitada: false);

        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!;

        Assert.Equal(0, chamadas);
    }

    [Fact]
    public async Task Laco_FalhaNaFaxina_EhLogada_EOServicoContinuaAteParar()
    {
        var log = new LogEmLista();
        var servico = Servico(new ArmazenamentoDeTeste(_ => throw new IOException("disco indisponível")), log);

        await servico.StartAsync(CancellationToken.None);
        for (var i = 0; i < 250 && !log.Entradas.Any(e => e.StartsWith("Error")); i++) await Task.Delay(20);
        await servico.StopAsync(CancellationToken.None);

        Assert.Contains(log.Entradas, e => e.StartsWith("Error") && e.Contains("Falha na faxina de fotos"));
        Assert.True(servico.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task Laco_RepeteAFaxinaACada24Horas_AteParar()
    {
        var faxinas = 0;
        var relogio = new RelogioManual();
        var servico = Servico(new ArmazenamentoDeTeste(_ =>
        {
            Interlocked.Increment(ref faxinas);
            return Task.FromResult(0);
        }), new LogEmLista(), relogio: relogio);

        await servico.StartAsync(CancellationToken.None);
        for (var i = 0; i < 250 && relogio.Pendentes == 0; i++) await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref faxinas));

        Assert.Equal(FotosCleanupService.Intervalo, relogio.Disparar());
        for (var i = 0; i < 250 && relogio.Pendentes == 0; i++) await Task.Delay(20);
        Assert.Equal(2, Volatile.Read(ref faxinas));

        await servico.StopAsync(CancellationToken.None);
        Assert.True(servico.ExecuteTask!.IsCompleted);
        Assert.Equal(2, Volatile.Read(ref faxinas));
    }

    [Fact]
    public async Task Laco_ParadaDuranteAFaxina_EncerraSemLogarErro()
    {
        var log = new LogEmLista();
        var iniciou = new TaskCompletionSource();
        var servico = Servico(new ArmazenamentoDeTeste(async ct =>
        {
            iniciou.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }), log);

        await servico.StartAsync(CancellationToken.None);
        await iniciou.Task;
        await servico.StopAsync(CancellationToken.None);

        Assert.True(servico.ExecuteTask!.IsCompleted);
        Assert.DoesNotContain(log.Entradas, e => e.StartsWith("Error"));
    }
}
