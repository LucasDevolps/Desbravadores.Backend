using System.Text;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Almirante.Api.Tests;

public sealed class ProcessadorFotosTests
{
    private static ProcessadorFotos Processador(Action<MediaOptions.ImageSettings>? ajustar = null)
    {
        var options = new MediaOptions();
        ajustar?.Invoke(options.Images);
        return new ProcessadorFotos(Microsoft.Extensions.Options.Options.Create(options), NullLogger<ProcessadorFotos>.Instance);
    }

    private static ApiProblemException Recusa(Func<FotoProcessada> processar) => Assert.Throws<ApiProblemException>(processar);

    public static TheoryData<byte[], FormatoFoto?> Assinaturas => new()
    {
        { [0xFF, 0xD8, 0xFF, 0xE0], FormatoFoto.Jpeg },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00], FormatoFoto.Png },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8], FormatoFoto.Webp },
        { [0xFF, 0xD8], null },
        { [0x00, 0xD8, 0xFF], null },
        { [0xFF, 0x00, 0xFF], null },
        { [0xFF, 0xD8, 0x00], null },
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A], null },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WEB"u8], null },
        { [.. "RIFX"u8, 0, 0, 0, 0, .. "WEBP"u8], null },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "AVI "u8], null },
        { [.. "GIF89a"u8], null },
        { [.. "MZ"u8, 0x90, 0x00], null },
        { [.. "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8], null },
        { [.. "%PDF-1.7"u8], null },
        { [.. "PK"u8, 0x03, 0x04], null },
        { [0, 0, 0, 0x18, .. "ftypheic"u8], null },
    };

    [Theory]
    [MemberData(nameof(Assinaturas))]
    public void DetectarAssinatura_ReconheceSoJpegPngEWebp(byte[] conteudo, FormatoFoto? esperado) =>
        Assert.Equal(esperado, ProcessadorFotos.DetectarAssinatura(conteudo));

    [Theory]
    [InlineData("MZ")]
    [InlineData("GIF89a")]
    [InlineData("<svg/>")]
    [InlineData("PK")]
    public void Processar_ArquivoQueNaoEhJpegPngWebp_Retorna415(string inicio)
    {
        var falso = Encoding.ASCII.GetBytes(inicio).Concat(new byte[200]).ToArray();

        var recusa = Recusa(() => Processador().Processar(falso, FormatoFoto.Jpeg, CancellationToken.None));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, recusa.Status);
    }

    [Fact]
    public void Processar_ConteudoDeOutroFormatoQueODeclarado_Retorna415()
    {
        var recusa = Recusa(() => Processador().Processar(FotosTestKit.Png(200, 200), FormatoFoto.Jpeg, CancellationToken.None));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, recusa.Status);
        Assert.Contains("não corresponde", recusa.Detail);
    }

    public static TheoryData<string, int, int, int, int> Redimensionamentos => new()
    {
        { "jpeg", 3000, 2000, 1024, 683 },
        { "jpeg", 1600, 1200, 1024, 768 },
        { "jpeg", 1100, 800, 1024, 745 },
        { "png", 1000, 3000, 341, 1024 },
        { "webp", 2000, 1024, 1024, 524 },
        { "png", 200, 100, 200, 100 },
        { "jpeg", 1024, 1024, 1024, 1024 },
    };

    [Theory]
    [MemberData(nameof(Redimensionamentos))]
    public void Processar_ImagemValida_ViraWebpQueCabeNoLimite_SemAmpliar(string formato, int largura, int altura, int esperadaL, int esperadaA)
    {
        var (bytes, declarado) = formato switch
        {
            "jpeg" => (FotosTestKit.Jpeg(largura, altura), FormatoFoto.Jpeg),
            "png" => (FotosTestKit.Png(largura, altura), FormatoFoto.Png),
            _ => (FotosTestKit.Webp(largura, altura), FormatoFoto.Webp),
        };

        var foto = Processador().Processar(bytes, declarado, CancellationToken.None);

        Assert.Equal(FormatoFoto.Webp, ProcessadorFotos.DetectarAssinatura(foto.Conteudo));
        Assert.Equal((esperadaL, esperadaA), (foto.Largura, foto.Altura));
        Assert.Equal((esperadaL, esperadaA), FotosTestKit.Dimensoes(foto.Conteudo));
        Assert.Equal(82, foto.Qualidade);
        Assert.True(foto.Conteudo.Length <= 1024 * 1024);
        Assert.True(FotosTestKit.Parecida(FotosTestKit.CorEm(foto.Conteudo, 2, 2), FotosTestKit.Vermelho));
    }

    [Theory]
    [InlineData(50, 200)]
    [InlineData(200, 50)]
    public void Processar_PequenaDemais_Retorna422(int largura, int altura)
    {
        var recusa = Recusa(() => Processador().Processar(FotosTestKit.Png(largura, altura), FormatoFoto.Png, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, recusa.Status);
        Assert.Contains("pequena demais", recusa.Detail);
    }

    [Fact]
    public void Processar_AcimaDosMegapixels_Retorna422()
    {
        var recusa = Recusa(() => Processador(i => i.MaxMegapixels = 1)
            .Processar(FotosTestKit.Jpeg(1200, 1000), FormatoFoto.Jpeg, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, recusa.Status);
        Assert.Contains("1 megapixels", recusa.Detail);
    }

    [Fact]
    public void Processar_PngBomba_EhRecusadoSoPeloCabecalho_SemDecodificar()
    {
        var bomba = FotosTestKit.PngBomba(50_000, 50_000);
        Assert.True(bomba.Length < 100);

        var inicio = System.Diagnostics.Stopwatch.StartNew();
        var recusa = Recusa(() => Processador().Processar(bomba, FormatoFoto.Png, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, recusa.Status);
        Assert.Contains("grande demais", recusa.Detail);
        Assert.True(inicio.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Processar_CabecalhoInvalido_Retorna422()
    {
        var lixo = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Encoding.ASCII.GetBytes(new string('x', 300))).ToArray();

        var recusa = Recusa(() => Processador().Processar(lixo, FormatoFoto.Png, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, recusa.Status);
    }

    [Fact]
    public void Processar_JpegTruncado_Retorna422()
    {
        using var ruido = FotosTestKit.Ruido(800, 600);
        var jpeg = FotosTestKit.Codificar(ruido, SKEncodedImageFormat.Jpeg);
        var truncado = jpeg[..(jpeg.Length * 6 / 10)];

        var recusa = Recusa(() => Processador().Processar(truncado, FormatoFoto.Jpeg, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, recusa.Status);
        Assert.Contains("corrompida ou incompleta", recusa.Detail);
    }

    [Fact]
    public void Processar_RemoveExifComGpsEOTextoEscondido()
    {
        var comExif = FotosTestKit.ComExif(FotosTestKit.Jpeg(400, 300), 1);
        Assert.True(FotosTestKit.Contem(comExif, FotosTestKit.MarcadorExif));

        var foto = Processador().Processar(comExif, FormatoFoto.Jpeg, CancellationToken.None);

        Assert.False(FotosTestKit.Contem(foto.Conteudo, FotosTestKit.MarcadorExif));
        Assert.False(FotosTestKit.Contem(foto.Conteudo, "Exif"));
        Assert.False(FotosTestKit.Contem(foto.Conteudo, "EXIF"));
    }

    [Fact]
    public void Processar_PerfilDeCorDisplayP3_ConverteParaSrgb_ESemPerfilNaSaida()
    {
        var p3 = FotosTestKit.JpegDisplayP3(300, 200);
        Assert.True(FotosTestKit.Contem(p3, "ICC_PROFILE"));

        var foto = Processador().Processar(p3, FormatoFoto.Jpeg, CancellationToken.None);

        Assert.False(FotosTestKit.Contem(foto.Conteudo, "ICCP"));
        Assert.True(FotosTestKit.Parecida(FotosTestKit.CorEm(foto.Conteudo, 2, 2), FotosTestKit.Vermelho, tolerancia: 80));
    }

    [Fact]
    public void Processar_PoliglotaJpegComZipAnexado_SoOsPixelsSobrevivem()
    {
        var zip = "PK\u0003\u0004conteudo-escondido-no-zip"u8.ToArray();
        var poliglota = FotosTestKit.Jpeg(300, 300).Concat(zip).ToArray();

        var foto = Processador().Processar(poliglota, FormatoFoto.Jpeg, CancellationToken.None);

        Assert.False(FotosTestKit.Contem(foto.Conteudo, "PK\u0003\u0004"));
        Assert.False(FotosTestKit.Contem(foto.Conteudo, "conteudo-escondido"));
    }

    public static TheoryData<ushort, string> Orientacoes => new()
    {
        { 1, "VmVdAzAm" }, { 2, "VdVmAmAz" }, { 3, "AmAzVdVm" }, { 4, "AzAmVmVd" },
        { 5, "VmAzVdAm" }, { 6, "AzVmAmVd" }, { 7, "AmVdAzVm" }, { 8, "VdAmVmAz" },
    };

    [Theory]
    [MemberData(nameof(Orientacoes))]
    public void Processar_AplicaAsOitoOrientacoesDoExif(ushort orientacao, string cantos)
    {
        var jpeg = FotosTestKit.ComExif(FotosTestKit.Jpeg(160, 100), orientacao);

        var foto = Processador().Processar(jpeg, FormatoFoto.Jpeg, CancellationToken.None);

        var gira = orientacao >= 5;
        var (largura, altura) = gira ? (100, 160) : (160, 100);
        Assert.Equal((largura, altura), (foto.Largura, foto.Altura));
        var cores = new Dictionary<string, SKColor>
        {
            ["Vm"] = FotosTestKit.Vermelho, ["Vd"] = FotosTestKit.Verde, ["Az"] = FotosTestKit.Azul, ["Am"] = FotosTestKit.Amarelo,
        };
        var pontos = new[] { (8, 8), (largura - 9, 8), (8, altura - 9), (largura - 9, altura - 9) };
        for (var i = 0; i < 4; i++)
        {
            var esperada = cores[cantos.Substring(i * 2, 2)];
            var real = FotosTestKit.CorEm(foto.Conteudo, pontos[i].Item1, pontos[i].Item2);
            Assert.True(FotosTestKit.Parecida(real, esperada), $"orientação {orientacao}, canto {i}: {real} em vez de {esperada}");
        }
    }

    [Fact]
    public void Processar_SaidaAcimaDoLimite_BaixaAQualidadeAteCaber()
    {
        using var ruido = FotosTestKit.Ruido(600, 600);
        var png = FotosTestKit.Codificar(ruido, SKEncodedImageFormat.Png);
        var referencia = Processador().Processar(png, FormatoFoto.Png, CancellationToken.None);

        var foto = Processador(i => i.MaxStoredSizeMb = (referencia.Conteudo.Length - 1) / (1024d * 1024d))
            .Processar(png, FormatoFoto.Png, CancellationToken.None);

        Assert.Equal(74, foto.Qualidade);
        Assert.True(foto.Conteudo.Length < referencia.Conteudo.Length);
    }

    [Fact]
    public void Processar_SaidaQueNaoCabeNemNaQualidadeMinima_Retorna422()
    {
        using var ruido = FotosTestKit.Ruido(600, 600);
        var png = FotosTestKit.Codificar(ruido, SKEncodedImageFormat.Png);

        var recusa = Recusa(() => Processador(i => i.MaxStoredSizeMb = 0.01).Processar(png, FormatoFoto.Png, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, recusa.Status);
        Assert.Contains("Não foi possível reduzir", recusa.Detail);
    }

    [Fact]
    public void Processar_PrazoEsgotado_Retorna422()
    {
        using var ruido = FotosTestKit.Ruido(2000, 2000);
        var png = FotosTestKit.Codificar(ruido, SKEncodedImageFormat.Png);

        var recusa = Recusa(() => Processador(i => i.ProcessingTimeoutMs = 1).Processar(png, FormatoFoto.Png, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, recusa.Status);
        Assert.Contains("demorou demais", recusa.Detail);
    }

    [Fact]
    public void Processar_CanceladoPeloCliente_PropagaOCancelamento_SemVirar422()
    {
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            Processador().Processar(FotosTestKit.Jpeg(300, 300), FormatoFoto.Jpeg, cancelado.Token));
    }

    [Fact]
    public void ExigirSaidaValida_AceitaSoWebpSemMetadadoEDentroDasDimensoes()
    {
        var processador = Processador();
        var saida = (processador.Processar(FotosTestKit.Jpeg(300, 200), FormatoFoto.Jpeg, CancellationToken.None)).Conteudo;
        processador.ExigirSaidaValida(saida);

        Assert.Throws<InvalidOperationException>(() => processador.ExigirSaidaValida(FotosTestKit.Jpeg(300, 200)));
        foreach (var metadado in new[] { "EXIF", "ICCP", "XMP " })
        {
            Assert.Throws<InvalidOperationException>(() => processador.ExigirSaidaValida(FotosTestKit.WebpComChunk(saida, metadado)));
        }

        var corrompido = saida[..20].Concat(new byte[100]).ToArray();
        Assert.Throws<InvalidOperationException>(() => processador.ExigirSaidaValida(corrompido));

        Assert.Throws<InvalidOperationException>(() => Processador(i => i.MaxWidth = 100).ExigirSaidaValida(saida));
        Assert.Throws<InvalidOperationException>(() => Processador(i => i.MaxHeight = 100).ExigirSaidaValida(saida));
    }

    [Fact]
    public void ExigirSaidaValida_TamanhoDeChunkForjado_SoEncerraALeitura()
    {
        var forjado = "RIFF"u8.ToArray().Concat(new byte[4]).Concat("WEBP"u8.ToArray())
            .Concat("VP8 "u8.ToArray()).Concat(BitConverter.GetBytes(uint.MaxValue)).Concat(new byte[16]).ToArray();

        Assert.Throws<InvalidOperationException>(() => Processador().ExigirSaidaValida(forjado));
    }
}
