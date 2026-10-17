using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Almirante.Api.Services;

public enum FormatoFoto
{
    Jpeg,
    Png,
    Webp,
}

public sealed record FotoProcessada(byte[] Conteudo, int Largura, int Altura, int Qualidade);

public sealed class ProcessadorFotos(IOptions<MediaOptions> options, ILogger<ProcessadorFotos> logger)
{
    private const int PassoQualidade = 8;

    private static readonly byte[] AssinaturaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly MediaOptions.ImageSettings _limites = options.Value.Images;

    private sealed class Prazo(TimeSpan limite, CancellationToken ct)
    {
        private readonly long _inicio = System.Diagnostics.Stopwatch.GetTimestamp();

        public void Conferir()
        {
            ct.ThrowIfCancellationRequested();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_inicio) > limite)
            {
                throw Invalida("A imagem demorou demais para ser processada; envie uma imagem menor ou mais simples.");
            }
        }
    }

    public FotoProcessada Processar(byte[] arquivo, FormatoFoto declarado, CancellationToken ct)
    {
        var formato = DetectarAssinatura(arquivo) ?? throw NaoSuportado("O arquivo não é uma imagem JPEG, PNG ou WebP.");
        if (formato != declarado)
        {
            throw NaoSuportado("O conteúdo do arquivo não corresponde à extensão/Content-Type informados.");
        }

        return Pipeline(arquivo, new Prazo(TimeSpan.FromMilliseconds(_limites.ProcessingTimeoutMs), ct));
    }

    public static FormatoFoto? DetectarAssinatura(ReadOnlySpan<byte> conteudo)
    {
        if (conteudo.Length >= 3 && conteudo[0] == 0xFF && conteudo[1] == 0xD8 && conteudo[2] == 0xFF)
        {
            return FormatoFoto.Jpeg;
        }

        if (conteudo.StartsWith(AssinaturaPng))
        {
            return FormatoFoto.Png;
        }

        return conteudo.Length >= 12 && conteudo[..4].SequenceEqual("RIFF"u8) && conteudo.Slice(8, 4).SequenceEqual("WEBP"u8)
            ? FormatoFoto.Webp
            : null;
    }

    public void ExigirSaidaValida(byte[] conteudo)
    {
        if (DetectarAssinatura(conteudo) != FormatoFoto.Webp || ChunksRiff(conteudo).Any(c => c is "EXIF" or "ICCP" or "XMP "))
        {
            throw new InvalidOperationException("A saída do reencode não é um WebP sem metadados.");
        }

        using var codec = SKCodec.Create(new MemoryStream(conteudo, writable: false))
            ?? throw new InvalidOperationException("A saída do reencode não pôde ser lida.");
        if (codec.Info.Width > _limites.MaxWidth || codec.Info.Height > _limites.MaxHeight)
        {
            throw new InvalidOperationException("A saída do reencode excede as dimensões configuradas.");
        }
    }

    private FotoProcessada Pipeline(byte[] arquivo, Prazo prazo)
    {
        using var codec = SKCodec.Create(new MemoryStream(arquivo, writable: false))
            ?? throw Invalida("A imagem está corrompida ou incompleta.");
        ExigirDimensoes(codec.Info.Width, codec.Info.Height);
        prazo.Conferir();

        var lado = Math.Max(_limites.MaxWidth, _limites.MaxHeight);
        var escala = Math.Min(1f, (float)lado / Math.Max(codec.Info.Width, codec.Info.Height));
        var dimensoes = codec.GetScaledDimensions(escala);
        while (escala < 1f && Math.Max(dimensoes.Width, dimensoes.Height) < lado)
        {
            escala = Math.Min(1f, escala + 0.125f);
            dimensoes = codec.GetScaledDimensions(escala);
        }

        var info = new SKImageInfo(dimensoes.Width, dimensoes.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var decodificada = new SKBitmap(info);
        if (codec.GetPixels(info, decodificada.GetPixels()) != SKCodecResult.Success)
        {
            throw Invalida("A imagem está corrompida ou incompleta.");
        }

        prazo.Conferir();
        using var orientada = Orientar(decodificada, codec.EncodedOrigin);
        using var final = Reduzir(orientada);
        prazo.Conferir();
        return Codificar(final, prazo);
    }

    private void ExigirDimensoes(int largura, int altura)
    {
        if (largura < _limites.MinWidth || altura < _limites.MinHeight)
        {
            throw Invalida($"A imagem é pequena demais: mínimo {_limites.MinWidth}x{_limites.MinHeight} pixels.");
        }

        if ((long)largura * altura > _limites.MaxPixels)
        {
            throw Invalida($"A imagem é grande demais: máximo {_limites.MaxMegapixels} megapixels.");
        }
    }

    private static SKBitmap Orientar(SKBitmap origem, SKEncodedOrigin orientacao)
    {
        float w = origem.Width, h = origem.Height;
        var (matriz, gira) = orientacao switch
        {
            SKEncodedOrigin.TopRight => (new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1), false),
            SKEncodedOrigin.BottomRight => (new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.BottomLeft => (new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.LeftTop => (new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightTop => (new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightBottom => (new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), true),
            SKEncodedOrigin.LeftBottom => (new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1), true),
            _ => (SKMatrix.Identity, false),
        };

        var destino = new SKBitmap(origem.Info.WithSize(gira ? origem.Height : origem.Width, gira ? origem.Width : origem.Height));
        using var canvas = new SKCanvas(destino);
        using var imagem = SKImage.FromBitmap(origem);
        canvas.SetMatrix(matriz);
        canvas.DrawImage(imagem, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return destino;
    }

    private SKBitmap Reduzir(SKBitmap origem)
    {
        var fator = Math.Min(1d, Math.Min((double)_limites.MaxWidth / origem.Width, (double)_limites.MaxHeight / origem.Height));
        var largura = Math.Max(1, (int)Math.Round(origem.Width * fator));
        var altura = Math.Max(1, (int)Math.Round(origem.Height * fator));
        return origem.Resize(origem.Info.WithSize(largura, altura), new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private FotoProcessada Codificar(SKBitmap imagem, Prazo prazo)
    {
        using var pixels = new SKPixmap(imagem.Info.WithColorSpace(null), imagem.GetPixels(), imagem.RowBytes);
        for (var qualidade = _limites.WebpQuality; ; qualidade = Math.Max(_limites.MinWebpQuality, qualidade - PassoQualidade))
        {
            prazo.Conferir();
            using var dados = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, qualidade))!;
            if (dados.Size <= _limites.MaxStoredBytes)
            {
                var conteudo = dados.ToArray();
                ExigirSaidaValida(conteudo);
                return new FotoProcessada(conteudo, imagem.Width, imagem.Height, qualidade);
            }

            if (qualidade == _limites.MinWebpQuality)
            {
                logger.LogInformation("Foto recusada: não coube em {Limite} MB nem com qualidade {Qualidade}.", _limites.MaxStoredSizeMb, qualidade);
                throw Invalida($"Não foi possível reduzir a foto a {_limites.MaxStoredSizeMb} MB sem perder qualidade demais; envie uma imagem menor.");
            }
        }
    }

    private static IEnumerable<string> ChunksRiff(byte[] riff)
    {
        for (var posicao = 12; posicao + 8 <= riff.Length;)
        {
            var tamanho = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(posicao + 4, 4));
            yield return System.Text.Encoding.ASCII.GetString(riff, posicao, 4);
            posicao = (int)Math.Min(riff.Length, posicao + 8L + tamanho + (tamanho & 1));
        }
    }

    private static ApiProblemException NaoSuportado(string detalhe) =>
        new(StatusCodes.Status415UnsupportedMediaType, "Formato de imagem não suportado.", detalhe);

    private static ApiProblemException Invalida(string detalhe) =>
        new(StatusCodes.Status422UnprocessableEntity, "Imagem inválida.", detalhe);
}
