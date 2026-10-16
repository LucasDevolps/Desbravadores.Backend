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

// Pipeline de segurança da foto (issue #116), do mais barato ao mais caro (Skia; decisão e mitigações no ADR-0011):
//   1. assinatura real (magic bytes) de JPEG/PNG/WebP, igual ao tipo declarado (extensão + Content-Type). Nada além
//      desses três formatos chega ao Skia (GIF, BMP, ICO, HEIF, RAW... nunca são decodificados);
//   2. só o CABEÇALHO (SKCodec, sem decodificar pixels): dimensões mínimas e máximo de megapixels. É aqui que morre a
//      imagem-bomba (ex.: PNG de poucos KB declarando 50000x50000), antes de qualquer alocação proporcional à imagem;
//   3. decodificação num bitmap de tamanho já conhecido e limitado: o JPEG decodifica reduzido (escala DCT), só o 1º
//      quadro de animações, cor convertida para sRGB, entrada truncada recusada;
//   4. orientação pelo EXIF, redução para caber em MaxWidth x MaxHeight (nunca amplia) e reencode em WebP sem nenhum
//      metadado (o Skia não grava EXIF e o pixmap vai sem perfil de cor), baixando a qualidade até caber em
//      MaxStoredSizeMb; a saída é conferida de novo (formato, dimensões e ausência de chunks EXIF/ICC/XMP).
// Só os pixels sobrevivem: conteúdo anexado (poliglota JPEG+ZIP/HTML) ou escondido em metadados não chega ao disco.
// A decodificação nativa não é interrompível: o prazo é conferido entre as etapas, e os limites acima mantêm cada etapa curta.
public sealed class ProcessadorFotos(IOptions<MediaOptions> options, ILogger<ProcessadorFotos> logger)
{
    private const int PassoQualidade = 8;

    private static readonly byte[] AssinaturaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly MediaOptions.ImageSettings _limites = options.Value.Images;

    // Prazo medido pelo relógio monotônico e conferido entre as etapas, junto com o cancelamento do cliente. Não usa
    // timer: um timer depende do thread pool, que sob carga (justamente quando o prazo importa) atrasa o disparo.
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

    // Síncrono de propósito: decodificação e reencode são CPU no código nativo do Skia, sem E/S para aguardar.
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

    // Defesa em profundidade sobre a SAÍDA do encoder: só um WebP dentro das dimensões configuradas e sem chunk de
    // metadado (EXIF, ICCP, XMP) vai para o disco.
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

        // Escala de decodificação: o JPEG reduz já no DCT, em oitavos, quando a imagem é maior que o necessário; os outros
        // formatos devolvem o próprio tamanho ou o tamanho exato pedido. Nunca amplia (escala máxima 1). O Skia arredonda
        // o JPEG para o oitavo MAIS PRÓXIMO, que pode ficar abaixo do alvo (1600x1200 com alvo 1024 daria 5/8 = 1000):
        // sobe um oitavo até alcançar o alvo, para a redução final partir de pixels suficientes.
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

    // As 8 orientações do EXIF (tag 0x0112), aplicadas com a matriz que leva o pixel (x, y) da imagem gravada para a
    // posição em que ele deve ser exibido. 5 a 8 giram 90 graus, então largura e altura se invertem.
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
        // Giro de 90 graus e espelhamento caem exatamente na grade de pixels: amostragem por vizinho mais próximo é exata.
        canvas.DrawImage(imagem, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return destino;
    }

    // Cabe em MaxWidth x MaxHeight mantendo a proporção; imagem que já cabe é só copiada (nunca ampliada).
    private SKBitmap Reduzir(SKBitmap origem)
    {
        var fator = Math.Min(1d, Math.Min((double)_limites.MaxWidth / origem.Width, (double)_limites.MaxHeight / origem.Height));
        var largura = Math.Max(1, (int)Math.Round(origem.Width * fator));
        var altura = Math.Max(1, (int)Math.Round(origem.Height * fator));
        return origem.Resize(origem.Info.WithSize(largura, altura), new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private FotoProcessada Codificar(SKBitmap imagem, Prazo prazo)
    {
        // Pixels já em sRGB; o pixmap vai SEM espaço de cor para o encoder não gravar perfil ICC.
        using var pixels = new SKPixmap(imagem.Info.WithColorSpace(null), imagem.GetPixels(), imagem.RowBytes);
        for (var qualidade = _limites.WebpQuality; ; qualidade = Math.Max(_limites.MinWebpQuality, qualidade - PassoQualidade))
        {
            prazo.Conferir();
            // O encoder só devolve null para pixmap inválido; este foi alocado e preenchido acima.
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

    // FourCC dos chunks de um RIFF (WebP): [FourCC][tamanho LE de 4 bytes][dados, alinhados em 2 bytes]. Um tamanho
    // forjado só encerra a leitura (o avanço é limitado ao fim do arquivo).
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
