using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

public sealed class LocalFileStorage(IOptions<MediaOptions> options) : IFileStorage
{
    private const string PrefixoIncompleto = ".tmp-";

    private readonly string _raiz = Path.GetFullPath(options.Value.Storage.Path!);

    public async Task SaveAsync(string chave, ReadOnlyMemory<byte> conteudo, CancellationToken ct)
    {
        var destino = Resolver(chave);
        var pasta = Path.GetDirectoryName(destino)!;
        DiretorioPrivado.Criar(pasta);
        ExigirSemLink(pasta);

        var incompleto = Path.Combine(pasta, PrefixoIncompleto + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var arquivo = new FileStream(incompleto, OpcoesDeGravacao()))
            {
                await arquivo.WriteAsync(conteudo, ct);
                arquivo.Flush(flushToDisk: true);
            }

            File.Move(incompleto, destino, overwrite: false);
        }
        catch
        {
            File.Delete(incompleto);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string chave, CancellationToken ct)
    {
        var caminho = Resolver(chave);
        if (!File.Exists(caminho))
        {
            return Task.FromResult<Stream?>(null);
        }

        ExigirSemLink(caminho);
        Stream conteudo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<Stream?>(conteudo);
    }

    public Task DeleteAsync(string chave, CancellationToken ct)
    {
        var caminho = Resolver(chave);
        if (File.Exists(caminho))
        {
            File.Delete(caminho);
            RemoverPastaSeVazia(Path.GetDirectoryName(caminho)!);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ArquivoArmazenado>> ListAsync(CancellationToken ct)
    {
        var fotos = ArquivosDasPastasDeUsuario()
            .Select(arquivo => (Arquivo: arquivo, Chave: $"{FotoChave.Prefixo}{Path.GetFileName(Path.GetDirectoryName(arquivo))}/{Path.GetFileName(arquivo)}"))
            .Where(item => FotoChave.FormatoValido(item.Chave))
            .Select(item => new ArquivoArmazenado(item.Chave, File.GetLastWriteTimeUtc(item.Arquivo)))
            .ToList();
        return Task.FromResult<IReadOnlyList<ArquivoArmazenado>>(fotos);
    }

    public Task<int> PurgeIncompleteAsync(DateTime anterioresAUtc, CancellationToken ct)
    {
        var removidos = 0;
        foreach (var arquivo in ArquivosDasPastasDeUsuario().Where(a => Path.GetFileName(a).StartsWith(PrefixoIncompleto, StringComparison.Ordinal)))
        {
            if (File.GetLastWriteTimeUtc(arquivo) < anterioresAUtc)
            {
                File.Delete(arquivo);
                removidos++;
            }
        }

        return Task.FromResult(removidos);
    }

    private string Resolver(string chave)
    {
        if (!FotoChave.FormatoValido(chave))
        {
            throw new ArgumentException("Chave de foto fora do formato usuarios/{id}/{aleatório}.webp.", nameof(chave));
        }

        return Path.Combine(_raiz, chave.Replace('/', Path.DirectorySeparatorChar));
    }

    private IEnumerable<string> ArquivosDasPastasDeUsuario()
    {
        var usuarios = Path.Combine(_raiz, "usuarios");
        if (!Directory.Exists(usuarios))
        {
            return [];
        }

        var semLinks = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return Directory.EnumerateDirectories(usuarios, "*", semLinks)
            .SelectMany(pasta => Directory.EnumerateFiles(pasta, "*", semLinks));
    }

    private void ExigirSemLink(string caminho)
    {
        for (var atual = caminho; atual.Length > _raiz.Length; atual = Path.GetDirectoryName(atual)!)
        {
            FileSystemInfo info = Directory.Exists(atual) ? new DirectoryInfo(atual) : new FileInfo(atual);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Link simbólico ou junção na pasta de fotos: acesso recusado.");
            }
        }
    }

    private static void RemoverPastaSeVazia(string pasta)
    {
        try
        {
            Directory.Delete(pasta);
        }
        catch (IOException)
        {
        }
    }

    private static FileStreamOptions OpcoesDeGravacao()
    {
        var opcoes = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            opcoes.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return opcoes;
    }
}
