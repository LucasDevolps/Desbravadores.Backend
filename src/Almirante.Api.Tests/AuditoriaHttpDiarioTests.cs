using System.Text;
using System.Text.Json;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Almirante.Api.Tests;

/// <summary>
/// Garantia de entrega da auditoria HTTP (issue #158): diário em disco, banco indisponível, reinício, segmentos órfãos, novas
/// tentativas sem duplicar, disco indisponível, saturação e sinalização no health check.
/// </summary>
public sealed class AuditoriaHttpDiarioTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "almirante-testes-diario", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _descartaveis = [];

    public void Dispose()
    {
        foreach (var item in _descartaveis) item.Dispose();
        if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
    }

    private static AuditoriaHttpOptions Opcoes(string pasta, Action<AuditoriaHttpOptions>? ajuste = null)
    {
        var opcoes = new AuditoriaHttpOptions { DiretorioPendentes = pasta, TamanhoLote = 2 };
        ajuste?.Invoke(opcoes);
        return opcoes;
    }

    private DiarioAuditoriaHttp Diario(AuditoriaHttpOptions opcoes)
    {
        var diario = new DiarioAuditoriaHttp(Microsoft.Extensions.Options.Options.Create(opcoes), new ConfigurationBuilder().Build(),
            TimeProvider.System, NullLogger<DiarioAuditoriaHttp>.Instance);
        _descartaveis.Add(diario);
        return diario;
    }

    private static GravadorAuditoriaHttp Gravador(DiarioAuditoriaHttp diario, IRepositorioAuditoriaHttp repositorio, AuditoriaHttpOptions opcoes) =>
        new(diario, repositorio, Microsoft.Extensions.Options.Options.Create(opcoes), TimeProvider.System, NullLogger<GravadorAuditoriaHttp>.Instance);

    private static AuditoriaHttp Registro() => new()
    {
        Id = Guid.CreateVersion7(),
        Url = "https://localhost/api/Lancamentos",
        MetodoHttp = "GET",
        Request = SanitizadorAuditoriaHttp.SemCorpo,
        Headers = "{}",
        DataRequestUtc = DateTimeOffset.UtcNow,
        IpUsuario = "127.0.0.1",
        IdentificacaoUsuario = AuditoriaHttp.Anonimo,
        TraceId = "teste",
        StatusCode = 200,
        EstadoCapturaRequest = EstadoCapturaHttp.SemCorpo,
        EstadoCapturaResponse = EstadoCapturaHttp.SemCorpo,
    };

    private string[] Segmentos() => Directory.Exists(_pasta)
        ? Directory.GetFiles(_pasta).Where(f => f.EndsWith(DiarioAuditoriaHttp.ExtensaoAtivo) || f.EndsWith(DiarioAuditoriaHttp.ExtensaoPendente)).ToArray()
        : [];

    private static Task<HealthCheckResult> SaudeAsync(DiarioAuditoriaHttp diario, GravadorAuditoriaHttp gravador)
    {
        var check = new AuditoriaHttpHealthCheck(diario, gravador, TimeProvider.System);
        return check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("auditoria-http", check, HealthStatus.Degraded, null) });
    }

    [Fact]
    public async Task BancoFora_RegistrosFicamEmDisco_EVaoTodosQuandoEleVolta()
    {
        var opcoes = Opcoes(_pasta);
        var diario = Diario(opcoes);
        var repositorio = new RepositorioFalso { Fora = true };
        var gravador = Gravador(diario, repositorio, opcoes);
        var registros = Enumerable.Range(0, 5).Select(_ => Registro()).ToList();
        foreach (var registro in registros) await diario.RegistrarAsync(registro);

        Assert.False(await gravador.GravarPendentesAsync(CancellationToken.None));
        Assert.False(await gravador.GravarPendentesAsync(CancellationToken.None));

        Assert.Empty(repositorio.Gravados);
        Assert.NotNull(gravador.FalhandoDesde);
        var linhas = Segmentos().SelectMany(File.ReadAllLines).ToList();
        Assert.Equal(registros.Select(r => r.Id).Order(), linhas.Select(l => JsonSerializer.Deserialize<AuditoriaHttp>(l)!.Id).Order());

        repositorio.Fora = false;
        Assert.True(await gravador.GravarPendentesAsync(CancellationToken.None));

        Assert.Equal(registros.Select(r => r.Id).Order(), repositorio.Gravados.Keys.Order());
        Assert.Empty(Segmentos());
        Assert.Equal(0, diario.BytesEmDisco);
        Assert.Null(gravador.FalhandoDesde);
    }

    [Fact]
    public async Task Reinicio_RetomaPendentesEOrfaoDeProcessoQueCaiu_IgnorandoLinhaIncompleta()
    {
        var opcoes = Opcoes(_pasta);
        var anterior = Diario(opcoes);
        var antesDoReinicio = new[] { Registro(), Registro() };
        foreach (var registro in antesDoReinicio) await anterior.RegistrarAsync(registro);
        anterior.Dispose();
        var orfaos = new[] { Registro(), Registro() };
        var linhasOrfas = string.Join("\n", orfaos.Select(r => JsonSerializer.Serialize(r))) + "\n{\"Id\":\"" + Guid.NewGuid() + "\",\"Url\":\"htt";
        await File.WriteAllTextAsync(Path.Combine(_pasta, $"20200101000000000000-999999-{Guid.NewGuid():N}{DiarioAuditoriaHttp.ExtensaoAtivo}"), linhasOrfas);

        var repositorio = new RepositorioFalso();
        var reiniciado = Diario(opcoes);
        Assert.True(reiniciado.BytesEmDisco > 0);
        Assert.True(await Gravador(reiniciado, repositorio, opcoes).GravarPendentesAsync(CancellationToken.None));

        Assert.Equal(antesDoReinicio.Concat(orfaos).Select(r => r.Id).Order(), repositorio.Gravados.Keys.Order());
        Assert.Empty(Segmentos());
    }

    [Fact]
    public async Task SegmentoAbertoPorOutroProcesso_NaoEhTocado()
    {
        var opcoes = Opcoes(_pasta);
        var outroProcesso = Diario(opcoes);
        await outroProcesso.RegistrarAsync(Registro());
        var repositorio = new RepositorioFalso();

        Assert.True(await Gravador(Diario(opcoes), repositorio, opcoes).GravarPendentesAsync(CancellationToken.None));

        Assert.Empty(repositorio.Gravados);
        Assert.Single(Segmentos());
    }

    [Fact]
    public async Task NovaTentativa_DoMesmoRegistro_NaoDuplicaNoBanco()
    {
        var opcoes = Opcoes(_pasta);
        var diario = Diario(opcoes);
        var banco = Guid.NewGuid().ToString();
        var servicos = new ServiceCollection().AddDbContext<AlmiranteDbContext>(o => o.UseInMemoryDatabase(banco)).BuildServiceProvider();
        _descartaveis.Add(servicos);
        var repositorio = new RepositorioAuditoriaHttp(servicos.GetRequiredService<IServiceScopeFactory>());
        var gravador = Gravador(diario, repositorio, opcoes);
        var registro = Registro();
        var outro = Registro();

        await diario.RegistrarAsync(registro);
        Assert.True(await gravador.GravarPendentesAsync(CancellationToken.None));
        await diario.RegistrarAsync(registro);
        await diario.RegistrarAsync(outro);
        await diario.RegistrarAsync(outro);
        Assert.True(await gravador.GravarPendentesAsync(CancellationToken.None));
        await repositorio.InserirAsync([registro, outro], CancellationToken.None);

        using var escopo = servicos.CreateScope();
        var ids = await escopo.ServiceProvider.GetRequiredService<AlmiranteDbContext>().AuditoriasHttp.Select(a => a.Id).ToListAsync();
        Assert.Equal(new[] { registro.Id, outro.Id }.Order(), ids.Order());
    }

    [Fact]
    public async Task DiscoIndisponivel_UsaReservaEmMemoria_ESinalizaNoHealthCheck()
    {
        Directory.CreateDirectory(_pasta);
        var arquivoNoLugarDaPasta = Path.Combine(_pasta, "nao-e-pasta");
        await File.WriteAllTextAsync(arquivoNoLugarDaPasta, "x");
        var opcoes = Opcoes(arquivoNoLugarDaPasta);
        var diario = Diario(opcoes);
        var repositorio = new RepositorioFalso { Fora = true };
        var gravador = Gravador(diario, repositorio, opcoes);
        var registros = new[] { Registro(), Registro(), Registro() };

        foreach (var registro in registros) await diario.RegistrarAsync(registro);

        Assert.False(diario.DiscoDisponivel);
        Assert.Equal(3, diario.NaReserva);
        Assert.Equal(HealthStatus.Degraded, (await SaudeAsync(diario, gravador)).Status);
        Assert.False(await gravador.GravarPendentesAsync(CancellationToken.None));
        Assert.Equal(3, diario.NaReserva);

        repositorio.Fora = false;
        Assert.True(await gravador.GravarPendentesAsync(CancellationToken.None));
        Assert.Equal(registros.Select(r => r.Id).Order(), repositorio.Gravados.Keys.Order());
        Assert.Equal(0, diario.NaReserva);
        Assert.Equal(0, diario.Descartados);
    }

    [Fact]
    public async Task Saturacao_DiarioCheioEReservaEsgotada_DescartaComContagemEHealthDegradado()
    {
        var opcoes = Opcoes(_pasta, o => { o.LimiteDiarioMb = 0; o.LimiteReservaMemoria = 2; });
        var diario = Diario(opcoes);
        var gravador = Gravador(diario, new RepositorioFalso { Fora = true }, opcoes);
        Assert.Equal(HealthStatus.Healthy, (await SaudeAsync(diario, gravador)).Status);

        foreach (var _ in Enumerable.Range(0, 5)) await diario.RegistrarAsync(Registro());

        Assert.Empty(Segmentos());
        Assert.Equal(2, diario.NaReserva);
        Assert.Equal(3, diario.Descartados);
        var saude = await SaudeAsync(diario, gravador);
        Assert.Equal(HealthStatus.Degraded, saude.Status);
        Assert.Contains("3 registros descartados", saude.Description);
    }

    [Fact]
    public async Task Diario_GravaSoOQueRecebeu_EmLinhasJsonLegiveis_SemCaractereForaDoLatin1()
    {
        var opcoes = Opcoes(_pasta);
        var diario = Diario(opcoes);
        var registro = Registro();
        registro.Response = SanitizadorAuditoriaHttp.MarcadorCorpo("ação concluída", "application/json", 10);

        await diario.RegistrarAsync(registro);
        await diario.RotacionarAsync();

        var conteudo = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Assert.Single(Segmentos())));
        Assert.EndsWith("\n", conteudo);
        Assert.Equal(registro.Id, JsonSerializer.Deserialize<AuditoriaHttp>(conteudo.TrimEnd('\n'))!.Id);
        Assert.All(conteudo, c => Assert.True(c <= 0xFF));
    }

    private sealed class RepositorioFalso : IRepositorioAuditoriaHttp
    {
        public bool Fora { get; set; }
        public Dictionary<Guid, AuditoriaHttp> Gravados { get; } = [];

        public Task InserirAsync(IReadOnlyList<AuditoriaHttp> registros, CancellationToken cancellationToken)
        {
            if (Fora) throw new TimeoutException("banco indisponível (simulado)");
            foreach (var registro in registros) Gravados.TryAdd(registro.Id, registro);
            return Task.CompletedTask;
        }
    }

    private DiarioAuditoriaHttp Diario(AuditoriaHttpOptions opcoes, TimeProvider tempo, CapturingLoggerFactory logs)
    {
        var diario = new DiarioAuditoriaHttp(Microsoft.Extensions.Options.Options.Create(opcoes), new ConfigurationBuilder().Build(), tempo,
            logs.CreateLogger<DiarioAuditoriaHttp>());
        _descartaveis.Add(diario);
        return diario;
    }

    private string SegmentoAtivo() => Assert.Single(Directory.GetFiles(_pasta, "*" + DiarioAuditoriaHttp.ExtensaoAtivo));

    [Fact]
    public void SemDiretorioConfigurado_UsaOPadraoDerivadoDaConexao()
    {
        var conexao = $"Server=127.0.0.1,1;Database=diario_{Guid.NewGuid():N}";
        var diario = new DiarioAuditoriaHttp(Microsoft.Extensions.Options.Options.Create(new AuditoriaHttpOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:almirante"] = conexao }).Build(),
            TimeProvider.System, NullLogger<DiarioAuditoriaHttp>.Instance);
        try
        {
            Assert.Equal(AuditoriaHttpOptions.DiretorioPadrao(conexao), diario.Diretorio);
            Assert.True(diario.DiscoDisponivel);
            Assert.True(Directory.Exists(diario.Diretorio));
        }
        finally
        {
            diario.Dispose();
            Directory.Delete(diario.Diretorio, recursive: true);
        }
    }

    [Fact]
    public async Task RotacaoQueFalha_MarcaODiscoIndisponivel_GuardaNaReservaAteAEspera_EVoltaAGravarDepois()
    {
        var logs = new CapturingLoggerFactory();
        var relogio = new RelogioManualDeTeste();
        var diario = Diario(Opcoes(_pasta), relogio, logs);
        await diario.RegistrarAsync(Registro());
        var ativo = SegmentoAtivo();
        // Uma pasta com o nome do segmento fechado faz a renomeação falhar (em qualquer sistema operacional).
        Directory.CreateDirectory(Path.ChangeExtension(ativo, DiarioAuditoriaHttp.ExtensaoPendente));

        await diario.RotacionarAsync();

        Assert.False(diario.DiscoDisponivel);
        Assert.Contains(logs.Entries, e => e.StartsWith("Error Diário da auditoria HTTP indisponível em", StringComparison.Ordinal) && e.Contains("IOException"));
        Assert.True(File.Exists(ativo), "o segmento que não foi renomeado continua em disco para ser retomado");

        relogio.Agora += TimeSpan.FromSeconds(29);
        await diario.RegistrarAsync(Registro());
        Assert.Equal(1, diario.NaReserva);

        relogio.Agora += TimeSpan.FromSeconds(1);
        await diario.RegistrarAsync(Registro());
        Assert.True(diario.DiscoDisponivel);
        Assert.Equal(1, diario.NaReserva);
        Assert.Contains(logs.Entries, e => e.StartsWith($"Information Diário da auditoria HTTP voltou a gravar em {_pasta}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PastaApagada_AoAbrirSegmento_VaiParaAReservaSemPerderORegistro()
    {
        var logs = new CapturingLoggerFactory();
        var diario = Diario(Opcoes(_pasta), new RelogioManualDeTeste(), logs);
        await diario.RegistrarAsync(Registro());
        await diario.RotacionarAsync();
        Directory.Delete(_pasta, recursive: true);

        var registro = Registro();
        await diario.RegistrarAsync(registro);

        Assert.False(diario.DiscoDisponivel);
        Assert.Equal(registro.Id, Assert.Single(diario.RetirarDaReserva(10)).Id);
        Assert.Contains(logs.Entries, e => e.Contains("DirectoryNotFoundException"));
    }

    [Fact]
    public async Task EscritaQueFalhaNoSegmentoAberto_NaoPerdeORegistro()
    {
        var logs = new CapturingLoggerFactory();
        var diario = Diario(Opcoes(_pasta), new RelogioManualDeTeste(), logs);
        await diario.RegistrarAsync(Registro());
        var ativo = SegmentoAtivo();

        // Outro processo trava o arquivo inteiro. No Windows a trava é obrigatória e a escrita falha; no Linux ela é só consultiva e a
        // escrita continua. Nos dois casos o registro não se perde: vai para o disco ou para a reserva em memória.
        using var leitor = new FileStream(ativo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (OperatingSystem.IsWindows()) leitor.Lock(0, long.MaxValue / 2);
        var registro = Registro();
        await diario.RegistrarAsync(registro);
        if (OperatingSystem.IsWindows()) leitor.Unlock(0, long.MaxValue / 2);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(diario.DiscoDisponivel);
            Assert.Equal(registro.Id, Assert.Single(diario.RetirarDaReserva(10)).Id);
        }
        else
        {
            Assert.True(diario.DiscoDisponivel);
            Assert.Equal(0, diario.NaReserva);
        }
    }

    [Fact]
    public async Task Encerramento_SemConseguirFecharOSegmento_AvisaEDeixaParaOProximoInicio()
    {
        var logs = new CapturingLoggerFactory();
        var diario = new DiarioAuditoriaHttp(Microsoft.Extensions.Options.Options.Create(Opcoes(_pasta)), new ConfigurationBuilder().Build(),
            TimeProvider.System, logs.CreateLogger<DiarioAuditoriaHttp>());
        await diario.RegistrarAsync(Registro());
        var ativo = SegmentoAtivo();
        Directory.CreateDirectory(Path.ChangeExtension(ativo, DiarioAuditoriaHttp.ExtensaoPendente));

        diario.Dispose();

        Assert.Contains(logs.Entries, e => e.StartsWith("Warning Segmento ativo da auditoria HTTP não pôde ser fechado no encerramento (IOException)",
            StringComparison.Ordinal));
        Assert.True(File.Exists(ativo));
        var pendentes = Diario(Opcoes(_pasta)).SegmentosPendentes().ToList();
        try
        {
            Assert.Single(pendentes, s => s.Caminho == ativo && s.Registros.Count == 1);
        }
        finally
        {
            foreach (var segmento in pendentes) segmento.Dispose();
        }
    }

    [Fact]
    public async Task Rejeitado_SemPastaDeRejeitados_EhDescartadoComContagem_EInformadoUmaVez()
    {
        var logs = new CapturingLoggerFactory();
        var diario = Diario(Opcoes(_pasta), TimeProvider.System, logs);
        await File.WriteAllTextAsync(Path.Combine(_pasta, DiarioAuditoriaHttp.PastaRejeitados), "arquivo no lugar da pasta");
        var registro = Registro();

        diario.Rejeitar(registro);
        diario.InformarDescartes();
        diario.InformarDescartes();

        Assert.Equal(1, diario.Descartados);
        Assert.Contains(logs.Entries, e => e.StartsWith($"Error Registro de auditoria HTTP {registro.Id} recusado pelo banco e não pôde ser guardado em rejeitados (IOException): descartado.",
            StringComparison.Ordinal));
        Assert.Single(logs.Entries, e => e.StartsWith("Error Auditoria HTTP descartou 1 registros (total 1 desde o início)", StringComparison.Ordinal));
    }
}
