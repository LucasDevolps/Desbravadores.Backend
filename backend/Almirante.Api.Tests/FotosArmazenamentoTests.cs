using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Services;

namespace Almirante.Api.Tests;

// Chave da foto (FotoChave) e armazenamento em disco (LocalFileStorage), direto nas classes, em pasta temporária própria.
public sealed class FotosArmazenamentoTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "almirante-testes-fotos", Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage _armazenamento;

    public FotosArmazenamentoTests()
    {
        Directory.CreateDirectory(_raiz);
        _armazenamento = new LocalFileStorage(Microsoft.Extensions.Options.Options.Create(new MediaOptions { Storage = { Path = _raiz } }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }

    private string Caminho(string chave) => Path.Combine(_raiz, chave.Replace('/', Path.DirectorySeparatorChar));

    // ------------------------------------------------------------------ FotoChave

    [Fact]
    public void FotoChave_GeraCaminhoRelativoNoFormatoEsperado_ComParteAleatoriaDiferenteACadaVez()
    {
        var usuario = Guid.NewGuid();
        var chave = FotoChave.Gerar(usuario);

        Assert.Matches($"^usuarios/{usuario:N}/[0-9a-f]{{32}}\\.webp$", chave);
        Assert.Equal(FotoChave.Tamanho, chave.Length);
        Assert.True(FotoChave.FormatoValido(chave));
        Assert.True(FotoChave.PertenceA(chave, usuario));
        Assert.False(FotoChave.PertenceA(chave, Guid.NewGuid()));
        Assert.NotEqual(chave, FotoChave.Gerar(usuario));
        Assert.Equal($"\"{chave.Substring(42, 32)}\"", FotoChave.ETag(chave));
    }

    public static TheoryData<string?> ChavesInvalidas()
    {
        var id = Guid.NewGuid().ToString("N");
        var aleatorio = Guid.NewGuid().ToString("N");
        return new()
        {
            null,
            "",
            $"usuarios/{id}/{aleatorio}.png",                    // outra extensão
            $"usuarios/{id.ToUpperInvariant()}/{aleatorio}.webp",  // maiúsculas
            $"usuarios/{id}/{aleatorio[..31]}..webp",             // ".." no nome
            $"usuarios\\{id}\\{aleatorio}.webp",                 // barra invertida
            $"/usuarios/{id}/{aleatorio}.web",                   // absoluto
            $"usuarios/../../{aleatorio}{aleatorio[..10]}.webp", // sai da raiz
            $"usuarios/{id}/{aleatorio}.webp\n",                 // quebra de linha no fim
            $"usuarios/{id}/{aleatorio}x.webp",                  // tamanho errado
        };
    }

    [Theory]
    [MemberData(nameof(ChavesInvalidas))]
    public void FotoChave_RecusaQualquerCoisaForaDoFormato(string? chave)
    {
        Assert.False(FotoChave.FormatoValido(chave));
        Assert.False(FotoChave.PertenceA(chave, Guid.NewGuid()));
    }

    // ------------------------------------------------------------------ gravação, leitura e exclusão

    [Fact]
    public async Task Gravar_Ler_Excluir_FluxoCompleto_SemSobrarTemporarioNemPastaVazia()
    {
        var chave = FotoChave.Gerar(Guid.NewGuid());
        var conteudo = new byte[] { 1, 2, 3, 4, 5 };

        await _armazenamento.SaveAsync(chave, conteudo, CancellationToken.None);

        Assert.Equal(conteudo, await File.ReadAllBytesAsync(Caminho(chave)));
        var pasta = Path.GetDirectoryName(Caminho(chave))!;
        Assert.Single(Directory.GetFiles(pasta));   // nenhum .tmp-* sobrando
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Caminho(chave)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(pasta));
        }

        await using (var lido = await _armazenamento.OpenReadAsync(chave, CancellationToken.None))
        {
            using var copia = new MemoryStream();
            await lido!.CopyToAsync(copia);
            Assert.Equal(conteudo, copia.ToArray());
        }

        await _armazenamento.DeleteAsync(chave, CancellationToken.None);
        Assert.False(File.Exists(Caminho(chave)));
        Assert.False(Directory.Exists(pasta));                                  // pasta do usuário vazia é removida
        Assert.Null(await _armazenamento.OpenReadAsync(chave, CancellationToken.None));
        await _armazenamento.DeleteAsync(chave, CancellationToken.None);       // idempotente
    }

    [Fact]
    public async Task Excluir_ComOutraFotoNaPasta_MantemAPasta()
    {
        var usuario = Guid.NewGuid();
        var primeira = FotoChave.Gerar(usuario);
        var segunda = FotoChave.Gerar(usuario);
        await _armazenamento.SaveAsync(primeira, new byte[] { 1 }, CancellationToken.None);
        await _armazenamento.SaveAsync(segunda, new byte[] { 2 }, CancellationToken.None);

        await _armazenamento.DeleteAsync(primeira, CancellationToken.None);

        Assert.True(File.Exists(Caminho(segunda)));
    }

    [Fact]
    public async Task Gravar_ChaveQueJaExiste_NaoSobrescreve_EApagaOTemporario()
    {
        var chave = FotoChave.Gerar(Guid.NewGuid());
        await _armazenamento.SaveAsync(chave, new byte[] { 1 }, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => _armazenamento.SaveAsync(chave, new byte[] { 2 }, CancellationToken.None));

        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Caminho(chave)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Caminho(chave))!));
    }

    [Fact]
    public async Task ChaveForaDoFormato_EhRecusadaEmTodasAsOperacoes()
    {
        const string invalida = "usuarios/../../segredo.webp";

        await Assert.ThrowsAsync<ArgumentException>(() => _armazenamento.SaveAsync(invalida, new byte[] { 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _armazenamento.OpenReadAsync(invalida, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _armazenamento.DeleteAsync(invalida, CancellationToken.None));
    }

    // ------------------------------------------------------------------ links simbólicos e junções

    [Fact]
    public async Task PastaDoUsuarioQueEhLink_EhRecusadaNaGravacao()
    {
        var usuario = Guid.NewGuid();
        var fora = Path.Combine(_raiz, "..", $"fora-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fora);
        try
        {
            Directory.CreateDirectory(Path.Combine(_raiz, "usuarios"));
            Directory.CreateSymbolicLink(Path.Combine(_raiz, "usuarios", usuario.ToString("N")), fora);

            await Assert.ThrowsAsync<IOException>(() => _armazenamento.SaveAsync(FotoChave.Gerar(usuario), new byte[] { 1 }, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(fora));
        }
        finally
        {
            Directory.Delete(fora, recursive: true);
        }
    }

    [Fact]
    public async Task ArquivoQueEhLinkParaForaDaRaiz_EhRecusadoNaLeitura()
    {
        var chave = FotoChave.Gerar(Guid.NewGuid());
        var segredo = Path.Combine(_raiz, "..", $"segredo-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(segredo, "fora da pasta de fotos");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho(chave))!);
            File.CreateSymbolicLink(Caminho(chave), segredo);

            await Assert.ThrowsAsync<IOException>(() => _armazenamento.OpenReadAsync(chave, CancellationToken.None));
        }
        finally
        {
            File.Delete(segredo);
        }
    }

    // ------------------------------------------------------------------ listagem e temporários (faxina)

    [Fact]
    public async Task Listar_DevolveSoFotosNoFormato_EPurge_ApagaSoTemporariosAntigos()
    {
        Assert.Empty(await _armazenamento.ListAsync(CancellationToken.None));     // ainda sem a pasta "usuarios"
        Assert.Equal(0, await _armazenamento.PurgeIncompleteAsync(DateTime.UtcNow, CancellationToken.None));

        var usuario = Guid.NewGuid();
        var foto = FotoChave.Gerar(usuario);
        await _armazenamento.SaveAsync(foto, new byte[] { 1 }, CancellationToken.None);
        var pasta = Path.GetDirectoryName(Caminho(foto))!;
        var anotacao = Path.Combine(pasta, "anotacao.txt");
        var tmpAntigo = Path.Combine(pasta, ".tmp-antigo");
        var tmpNovo = Path.Combine(pasta, ".tmp-novo");
        foreach (var arquivo in new[] { anotacao, tmpAntigo, tmpNovo }) await File.WriteAllBytesAsync(arquivo, [9]);
        File.SetLastWriteTimeUtc(tmpAntigo, DateTime.UtcNow.AddHours(-2));

        var listadas = await _armazenamento.ListAsync(CancellationToken.None);
        var removidos = await _armazenamento.PurgeIncompleteAsync(DateTime.UtcNow.AddHours(-1), CancellationToken.None);

        Assert.Equal(foto, Assert.Single(listadas).Chave);
        Assert.Equal(1, removidos);
        Assert.False(File.Exists(tmpAntigo));
        Assert.True(File.Exists(tmpNovo));
        Assert.True(File.Exists(anotacao));   // arquivo fora do formato nunca é tocado
    }
}
