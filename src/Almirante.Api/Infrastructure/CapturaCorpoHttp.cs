using System.IO.Pipelines;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Http.Features;

namespace Almirante.Api.Infrastructure;

/// <summary>
/// Cópia limitada de um corpo HTTP para a auditoria (issue #158). Só guarda bytes quando o corpo é JSON de rota comum; os demais só
/// contam o tamanho. Passou do limite, a cópia é descartada (nunca se sanitiza um JSON pela metade) e fica só o motivo.
/// </summary>
public sealed class CapturaCorpoHttp(int limite, EstadoCapturaHttp? omissao)
{
    private MemoryStream? _copia = omissao is null ? new MemoryStream() : null;

    /// <summary>Por que o conteúdo não é copiado (segurança, tipo), ou nulo quando é.</summary>
    public EstadoCapturaHttp? Omissao { get; } = omissao;

    public long Bytes { get; private set; }
    public bool Excedeu { get; private set; }

    public ReadOnlyMemory<byte> Conteudo => _copia is null ? ReadOnlyMemory<byte>.Empty : _copia.GetBuffer().AsMemory(0, (int)_copia.Length);

    public void Observar(ReadOnlySpan<byte> dados)
    {
        Bytes += dados.Length;
        if (_copia is null || dados.IsEmpty)
        {
            return;
        }

        if (_copia.Length + dados.Length > limite)
        {
            Excedeu = true;
            _copia = null;
            return;
        }

        _copia.Write(dados);
    }

    public void SomarBytes(long bytes) => Bytes += bytes;
}

/// <summary>
/// Corpo da requisição visto pela auditoria: repassa cada leitura da API sem ler nada por conta própria (não consome, não bufferiza o
/// upload) e copia o que passar, até o limite. Se a API não ler o corpo, a auditoria registra NaoLida.
/// </summary>
public sealed class LeituraAuditadaStream(Stream interno, CapturaCorpoHttp captura) : Stream
{
    public bool FimAtingido { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Registrar(interno.Read(buffer, offset, count), buffer.AsSpan(offset));

    public override int Read(Span<byte> buffer) => Registrar(interno.Read(buffer), buffer);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Registrar(await interno.ReadAsync(buffer.AsMemory(offset, count), cancellationToken), buffer.AsSpan(offset));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var lidos = await interno.ReadAsync(buffer, cancellationToken);
        return Registrar(lidos, buffer.Span);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Registrar(int lidos, ReadOnlySpan<byte> buffer)
    {
        if (lidos == 0)
        {
            FimAtingido = true;
        }
        else
        {
            captura.Observar(buffer[..lidos]);
        }

        return lidos;
    }
}

/// <summary>
/// Corpo da resposta visto pela auditoria: cada escrita segue na hora para o cliente (streaming e downloads não esperam o fim) e é
/// copiada até o limite. Arquivos enviados com SendFile seguem pelo caminho original, contados só pelo tamanho. O tipo de conteúdo é
/// decidido na primeira escrita, quando os cabeçalhos já estão definidos.
/// </summary>
public sealed class EscritaAuditadaStream(IHttpResponseBodyFeature interno, Func<CapturaCorpoHttp> decidir) : Stream, IHttpResponseBodyFeature
{
    private CapturaCorpoHttp? _captura;
    private PipeWriter? _writer;

    public CapturaCorpoHttp? Captura => _captura;
    public bool UsouSendFile { get; private set; }

    Stream IHttpResponseBodyFeature.Stream => this;

    PipeWriter IHttpResponseBodyFeature.Writer => _writer ??= PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen: true));

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void DisableBuffering() => interno.DisableBuffering();

    public Task StartAsync(CancellationToken cancellationToken = default) => interno.StartAsync(cancellationToken);

    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        UsouSendFile = true;
        Decidida().SomarBytes(count ?? Math.Max(0, new FileInfo(path).Length - offset));
        await interno.SendFileAsync(path, offset, count, cancellationToken);
    }

    public async Task CompleteAsync()
    {
        await FinalizarAsync();
        await interno.CompleteAsync();
    }

    /// <summary>Esvazia o adaptador de PipeWriter (o que a API escreveu por BodyWriter) antes de devolver o corpo original.</summary>
    public async Task FinalizarAsync()
    {
        if (_writer is not null)
        {
            await _writer.CompleteAsync();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Decidida().Observar(buffer);
        interno.Stream.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Decidida().Observar(buffer.Span);
        return interno.Stream.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => interno.Stream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => interno.Stream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private CapturaCorpoHttp Decidida() => _captura ??= decidir();
}
