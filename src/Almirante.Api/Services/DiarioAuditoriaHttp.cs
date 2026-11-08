using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

/// <summary>
/// Diário em disco da auditoria HTTP (issue #158): cada chamada, já sanitizada, vira uma linha JSON no segmento ativo deste processo,
/// com flush para o sistema operacional antes de a requisição terminar, o que sobrevive à queda do processo e do banco. O
/// <see cref="GravadorAuditoriaHttp"/> fecha o segmento a cada ciclo, insere os registros e só então apaga o arquivo; segmentos que
/// sobram de um processo anterior (reinício, queda, reciclagem do IIS) são retomados por qualquer processo que consiga abri-los com
/// exclusividade. Disco indisponível ou cheio (<see cref="AuditoriaHttpOptions.LimiteDiarioMb"/>): os registros vão para uma reserva em
/// memória limitada; esgotada a reserva, são descartados com erro no log e health check degradado. Nunca grava conteúdo não sanitizado
/// e nunca escreve conteúdo de registro no log.
/// </summary>
public sealed class DiarioAuditoriaHttp : IDisposable
{
    public const string ExtensaoAtivo = ".ativo";
    public const string ExtensaoPendente = ".pendente";
    public const string PastaRejeitados = "rejeitados";

    private static readonly JsonSerializerOptions OpcoesJson = new() { Encoder = SanitizadorAuditoriaHttp.Codificador };
    private static readonly TimeSpan EsperaDisco = TimeSpan.FromSeconds(30);

    private readonly AuditoriaHttpOptions _opcoes;
    private readonly TimeProvider _tempo;
    private readonly ILogger<DiarioAuditoriaHttp> _logger;
    private readonly SemaphoreSlim _trava = new(1, 1);
    private readonly ConcurrentQueue<AuditoriaHttp> _reserva = new();
    private FileStream? _segmento;
    private string? _caminhoSegmento;
    private long _bytesEmDisco;
    private int _naReserva;
    private long _descartados;
    private long _descartadosInformados;
    private int _avisoLimite;
    private DateTimeOffset? _discoIndisponivelDesde;
    private DateTimeOffset _proximaTentativaDisco;

    public DiarioAuditoriaHttp(IOptions<AuditoriaHttpOptions> opcoes, IConfiguration configuracao, TimeProvider tempo,
        ILogger<DiarioAuditoriaHttp> logger)
    {
        _opcoes = opcoes.Value;
        _tempo = tempo;
        _logger = logger;
        Diretorio = string.IsNullOrWhiteSpace(_opcoes.DiretorioPendentes)
            ? AuditoriaHttpOptions.DiretorioPadrao(configuracao.GetConnectionString("almirante"))
            : Path.GetFullPath(_opcoes.DiretorioPendentes);
        try
        {
            DiretorioPrivado.Criar(Diretorio);
            _bytesEmDisco = Directory.EnumerateFiles(Diretorio).Where(Segmento).Sum(arquivo => new FileInfo(arquivo).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarcarDiscoIndisponivel(ex);
        }
    }

    public string Diretorio { get; }
    public bool DiscoDisponivel => _discoIndisponivelDesde is null;
    public long BytesEmDisco => Interlocked.Read(ref _bytesEmDisco);
    public int NaReserva => Volatile.Read(ref _naReserva);
    public long Descartados => Interlocked.Read(ref _descartados);

    /// <summary>Registra uma chamada já sanitizada: no diário em disco ou, se ele falhar ou estiver cheio, na reserva em memória.</summary>
    public async Task RegistrarAsync(AuditoriaHttp registro)
    {
        if (!await TentarGravarEmDiscoAsync(Serializar(registro)))
        {
            GuardarNaReserva(registro);
        }
    }

    /// <summary>Fecha o segmento ativo (fsync) e o deixa pendente para o gravador; o próximo registro abre outro.</summary>
    public async Task RotacionarAsync()
    {
        await _trava.WaitAsync();
        try
        {
            FecharSegmento();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarcarDiscoIndisponivel(ex);
        }
        finally
        {
            _trava.Release();
        }
    }

    /// <summary>Segmentos fechados (deste ou de processos anteriores), do mais antigo ao mais novo, cada um aberto com exclusividade.</summary>
    public IEnumerable<SegmentoAuditoriaHttp> SegmentosPendentes()
    {
        string[] arquivos;
        try
        {
            arquivos = Directory.EnumerateFiles(Diretorio).Where(Segmento).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarcarDiscoIndisponivel(ex);
            yield break;
        }

        foreach (var caminho in arquivos)
        {
            if (string.Equals(caminho, _caminhoSegmento, StringComparison.Ordinal))
            {
                continue;
            }

            FileStream fluxo;
            try
            {
                // Outro processo vivo (ex.: reciclagem sobreposta do IIS) mantém o próprio segmento ativo aberto: a abertura falha e ele é pulado.
                fluxo = new FileStream(caminho, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return new SegmentoAuditoriaHttp(this, caminho, fluxo);
        }
    }

    public IReadOnlyList<AuditoriaHttp> RetirarDaReserva(int maximo)
    {
        var lote = new List<AuditoriaHttp>();
        while (lote.Count < maximo && _reserva.TryDequeue(out var registro))
        {
            Interlocked.Decrement(ref _naReserva);
            lote.Add(registro);
        }

        return lote;
    }

    /// <summary>Registros da reserva que o banco não aceitou agora: para o disco, se ele voltou, ou de volta à reserva.</summary>
    public async Task DevolverAsync(IEnumerable<AuditoriaHttp> registros)
    {
        foreach (var registro in registros)
        {
            await RegistrarAsync(registro);
        }
    }

    /// <summary>Registro que o banco recusa de forma permanente (constraint, tamanho): guardado à parte para análise, nunca reenviado.</summary>
    public void Rejeitar(AuditoriaHttp registro)
    {
        try
        {
            var pasta = Path.Combine(Diretorio, PastaRejeitados);
            DiretorioPrivado.Criar(pasta);
            var linha = Serializar(registro);
            _trava.Wait();
            try
            {
                using var fluxo = new FileStream(Path.Combine(pasta, $"{_tempo.GetUtcNow():yyyyMMdd}.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
                fluxo.Write(linha);
            }
            finally
            {
                _trava.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _descartados);
            _logger.LogError("Registro de auditoria HTTP {Id} recusado pelo banco e não pôde ser guardado em {Pasta} ({Tipo}): descartado.",
                registro.Id, PastaRejeitados, ex.GetType().Name);
        }
    }

    /// <summary>Resumo periódico dos descartes (cada descarte não gera uma linha de log, para o log não virar outro gargalo).</summary>
    public void InformarDescartes()
    {
        var total = Descartados;
        var anterior = Interlocked.Exchange(ref _descartadosInformados, total);
        if (total > anterior)
        {
            _logger.LogError("Auditoria HTTP descartou {Novos} registros (total {Total} desde o início): diário em disco e reserva em memória " +
                "esgotados. Verifique o banco, o espaço em {Diretorio} e AuditoriaHttp:LimiteDiarioMb/LimiteReservaMemoria.",
                total - anterior, total, Diretorio);
        }
    }

    public void Dispose()
    {
        _trava.Wait();
        try
        {
            FecharSegmento();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Segmento ativo da auditoria HTTP não pôde ser fechado no encerramento ({Tipo}); será retomado no próximo início.",
                ex.GetType().Name);
        }
        finally
        {
            _trava.Release();
        }
    }

    internal static AuditoriaHttp? Desserializar(string linha)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditoriaHttp>(linha, OpcoesJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal void Descontar(long bytes) => Interlocked.Add(ref _bytesEmDisco, -bytes);

    private static byte[] Serializar(AuditoriaHttp registro) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(registro, OpcoesJson) + "\n");

    private static bool Segmento(string caminho) =>
        caminho.EndsWith(ExtensaoPendente, StringComparison.Ordinal) || caminho.EndsWith(ExtensaoAtivo, StringComparison.Ordinal);

    private async Task<bool> TentarGravarEmDiscoAsync(byte[] linha)
    {
        if (!DiscoDisponivel && _tempo.GetUtcNow() < _proximaTentativaDisco)
        {
            return false;
        }

        if (BytesEmDisco + linha.Length > _opcoes.LimiteDiarioBytes)
        {
            if (Interlocked.Exchange(ref _avisoLimite, 1) == 0)
            {
                _logger.LogError("Diário da auditoria HTTP cheio ({Limite} MB em {Diretorio}): o banco não está acompanhando. Novos registros " +
                    "vão para a reserva em memória até o gravador esvaziar o diário.", _opcoes.LimiteDiarioMb, Diretorio);
            }

            return false;
        }

        await _trava.WaitAsync();
        try
        {
            _segmento ??= AbrirSegmento();
            await _segmento.WriteAsync(linha);
            await _segmento.FlushAsync();
            Interlocked.Add(ref _bytesEmDisco, linha.Length);
            Volatile.Write(ref _avisoLimite, 0);
            if (!DiscoDisponivel)
            {
                _discoIndisponivelDesde = null;
                _logger.LogInformation("Diário da auditoria HTTP voltou a gravar em {Diretorio}.", Diretorio);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DescartarSegmentoComFalha();
            MarcarDiscoIndisponivel(ex);
            return false;
        }
        finally
        {
            _trava.Release();
        }
    }

    private void GuardarNaReserva(AuditoriaHttp registro)
    {
        if (Interlocked.Increment(ref _naReserva) <= _opcoes.LimiteReservaMemoria)
        {
            _reserva.Enqueue(registro);
            return;
        }

        Interlocked.Decrement(ref _naReserva);
        if (Interlocked.Increment(ref _descartados) == 1)
        {
            _logger.LogError("Auditoria HTTP começou a descartar registros (primeiro: {Id}): diário em disco e reserva em memória esgotados.", registro.Id);
        }
    }

    private FileStream AbrirSegmento()
    {
        _caminhoSegmento = Path.Combine(Diretorio, $"{_tempo.GetUtcNow():yyyyMMddHHmmssfffffff}-{Environment.ProcessId}-{Guid.NewGuid():N}{ExtensaoAtivo}");
        var opcoes = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            opcoes.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(_caminhoSegmento, opcoes);
    }

    private void FecharSegmento()
    {
        if (_segmento is null)
        {
            return;
        }

        var caminho = _caminhoSegmento!;
        try
        {
            _segmento.Flush(flushToDisk: true);
        }
        finally
        {
            _segmento.Dispose();
            _segmento = null;
            _caminhoSegmento = null;
        }

        File.Move(caminho, Path.ChangeExtension(caminho, ExtensaoPendente));
    }

    private void DescartarSegmentoComFalha()
    {
        try
        {
            _segmento?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O que chegou ao arquivo continua lá e é retomado como segmento órfão; a linha incompleta é ignorada na leitura.
        }

        _segmento = null;
        _caminhoSegmento = null;
    }

    private void MarcarDiscoIndisponivel(Exception ex)
    {
        _proximaTentativaDisco = _tempo.GetUtcNow() + EsperaDisco;
        if (_discoIndisponivelDesde is null)
        {
            _discoIndisponivelDesde = _tempo.GetUtcNow();
            _logger.LogError("Diário da auditoria HTTP indisponível em {Diretorio} ({Tipo}): registros vão para a reserva em memória (sem " +
                "durabilidade se o processo cair) até o disco voltar. Ajuste AuditoriaHttp:DiretorioPendentes e as permissões da pasta.",
                Diretorio, ex.GetType().Name);
        }
    }
}

/// <summary>Segmento fechado do diário, aberto com exclusividade enquanto o gravador o processa.</summary>
public sealed class SegmentoAuditoriaHttp : IDisposable
{
    private readonly DiarioAuditoriaHttp _diario;
    private readonly FileStream _fluxo;

    internal SegmentoAuditoriaHttp(DiarioAuditoriaHttp diario, string caminho, FileStream fluxo)
    {
        _diario = diario;
        _fluxo = fluxo;
        Caminho = caminho;
        var registros = new List<AuditoriaHttp>();
        using var leitor = new StreamReader(fluxo, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        while (leitor.ReadLine() is { } linha)
        {
            if (linha.Length == 0)
            {
                continue;
            }

            if (DiarioAuditoriaHttp.Desserializar(linha) is { } registro)
            {
                registros.Add(registro);
            }
            else
            {
                LinhasInvalidas++;
            }
        }

        Registros = registros;
    }

    public string Caminho { get; }
    public IReadOnlyList<AuditoriaHttp> Registros { get; }

    /// <summary>Linhas que não são um registro completo (escrita interrompida por queda do processo ou do disco).</summary>
    public int LinhasInvalidas { get; }

    /// <summary>Todos os registros estão no banco: apaga o arquivo.</summary>
    public void Concluir()
    {
        var tamanho = _fluxo.Length;
        _fluxo.Dispose();
        File.Delete(Caminho);
        _diario.Descontar(tamanho);
    }

    public void Dispose() => _fluxo.Dispose();
}
