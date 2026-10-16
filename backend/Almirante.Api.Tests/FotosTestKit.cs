using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using SkiaSharp;

namespace Almirante.Api.Tests;

// Imagens SINTÉTICAS para os testes de foto (nunca foto de pessoa): quadrantes coloridos para conferir orientação, ruído
// para forçar a compressão, JPEG com EXIF (orientação + GPS + um texto marcador) e PNG-bomba montado byte a byte.
public static class FotosTestKit
{
    public static readonly SKColor Vermelho = new(220, 20, 20);
    public static readonly SKColor Verde = new(20, 200, 20);
    public static readonly SKColor Azul = new(20, 20, 220);
    public static readonly SKColor Amarelo = new(230, 220, 20);

    // Texto gravado no EXIF: não pode sobreviver ao reencode.
    public const string MarcadorExif = "SEGREDO-GPS-123";

    public static SKBitmap Quadrantes(int largura, int altura, SKColorSpace? espacoDeCor = null)
    {
        var bitmap = new SKBitmap(new SKImageInfo(largura, altura, SKColorType.Rgba8888, SKAlphaType.Premul, espacoDeCor));
        using var canvas = new SKCanvas(bitmap);
        using var tinta = new SKPaint();
        void Pintar(SKColor cor, float x, float y)
        {
            tinta.Color = cor;
            canvas.DrawRect(x, y, largura / 2f, altura / 2f, tinta);
        }

        Pintar(Vermelho, 0, 0);
        Pintar(Verde, largura / 2f, 0);
        Pintar(Azul, 0, altura / 2f);
        Pintar(Amarelo, largura / 2f, altura / 2f);
        return bitmap;
    }

    public static SKBitmap Ruido(int largura, int altura, int semente = 42)
    {
        var bitmap = new SKBitmap(new SKImageInfo(largura, altura, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var aleatorio = new Random(semente);
        var pixels = new byte[largura * altura * 4];
        aleatorio.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    public static byte[] Codificar(SKBitmap bitmap, SKEncodedImageFormat formato, int qualidade = 90)
    {
        using var imagem = SKImage.FromBitmap(bitmap);
        using var dados = imagem.Encode(formato, qualidade);
        return dados.ToArray();
    }

    public static byte[] Jpeg(int largura, int altura)
    {
        using var bitmap = Quadrantes(largura, altura);
        return Codificar(bitmap, SKEncodedImageFormat.Jpeg);
    }

    public static byte[] Png(int largura, int altura)
    {
        using var bitmap = Quadrantes(largura, altura);
        return Codificar(bitmap, SKEncodedImageFormat.Png);
    }

    public static byte[] Webp(int largura, int altura)
    {
        using var bitmap = Quadrantes(largura, altura);
        return Codificar(bitmap, SKEncodedImageFormat.Webp);
    }

    // JPEG com perfil de cor Display P3 embutido (APP2 ICC_PROFILE): a API converte para sRGB e não grava perfil.
    public static byte[] JpegDisplayP3(int largura, int altura)
    {
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var bitmap = Quadrantes(largura, altura, p3);
        return Codificar(bitmap, SKEncodedImageFormat.Jpeg);
    }

    // Insere logo após o SOI um APP1 "Exif" com: ImageDescription (marcador), Orientation e um IFD de GPS.
    public static byte[] ComExif(byte[] jpeg, ushort orientacao)
    {
        var descricao = Encoding.ASCII.GetBytes(MarcadorExif + "\0");
        var tiff = new byte[66 + 30];
        void U16(int pos, int valor) => BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(pos), (ushort)valor);
        void U32(int pos, int valor) => BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(pos), (uint)valor);
        void Entrada(int pos, int tag, int tipo, int quantidade, int valor) { U16(pos, tag); U16(pos + 2, tipo); U32(pos + 4, quantidade); U32(pos + 8, valor); }

        "II"u8.CopyTo(tiff); U16(2, 42); U32(4, 8);           // cabeçalho TIFF little-endian, IFD0 em 8
        U16(8, 3);                                              // IFD0: 3 entradas, em ordem de tag
        Entrada(10, 0x010E, 2, descricao.Length, 50);           // ImageDescription -> texto em 50
        Entrada(22, 0x0112, 3, 1, orientacao);                  // Orientation (SHORT no próprio campo)
        Entrada(34, 0x8825, 4, 1, 66);                          // ponteiro para o IFD de GPS em 66
        U32(46, 0);                                             // fim da cadeia de IFDs
        descricao.CopyTo(tiff, 50);
        U16(66, 2);                                             // IFD de GPS: 2 entradas
        Entrada(68, 0x0000, 1, 4, 0x00000202);                  // GPSVersionID 2.2.0.0
        Entrada(80, 0x0001, 2, 2, 'S');                         // GPSLatitudeRef "S"
        U32(92, 0);

        var app1 = new List<byte> { 0xFF, 0xE1 };
        var tamanho = 2 + 6 + tiff.Length;
        app1.Add((byte)(tamanho >> 8));
        app1.Add((byte)tamanho);
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(tiff);
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    // PNG válido no cabeçalho, declarando largura x altura, com um IDAT minúsculo: decodificá-lo exigiria alocar
    // largura x altura x 4 bytes. A API precisa recusá-lo só pelo cabeçalho.
    public static byte[] PngBomba(int largura, int altura)
    {
        var saida = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)largura);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)altura);
        ihdr[8] = 8;   // 8 bits por canal
        ihdr[9] = 6;   // RGBA
        Chunk(saida, "IHDR", ihdr);
        Chunk(saida, "IDAT", [0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]);
        Chunk(saida, "IEND", []);
        return [.. saida];
    }

    private static void Chunk(List<byte> saida, string tipo, byte[] dados)
    {
        var tamanho = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tamanho, (uint)dados.Length);
        saida.AddRange(tamanho);
        var corpo = Encoding.ASCII.GetBytes(tipo).Concat(dados).ToArray();
        saida.AddRange(corpo);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(corpo));
        saida.AddRange(crc);
    }

    private static uint Crc32(byte[] dados)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in dados)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    // RIFF/WEBP montado à mão com um chunk de metadado (para provar que a conferência da saída o recusa).
    public static byte[] WebpComChunk(byte[] webpValido, string fourCc)
    {
        var dados = new byte[] { 1, 2, 3 };   // tamanho ímpar: exige o byte de alinhamento
        var chunk = Encoding.ASCII.GetBytes(fourCc).Concat(BitConverter.GetBytes((uint)dados.Length)).Concat(dados).Append((byte)0);
        var corpo = webpValido[12..].Concat(chunk).ToArray();
        var cabecalho = "RIFF"u8.ToArray().Concat(BitConverter.GetBytes((uint)(corpo.Length + 4))).Concat("WEBP"u8.ToArray());
        return [.. cabecalho, .. corpo];
    }

    public static SKColor CorEm(byte[] imagem, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(imagem);
        return bitmap.GetPixel(x, y);
    }

    public static (int Largura, int Altura) Dimensoes(byte[] imagem)
    {
        using var codec = SKCodec.Create(new MemoryStream(imagem));
        return (codec.Info.Width, codec.Info.Height);
    }

    public static bool Parecida(SKColor real, SKColor esperada, int tolerancia = 60) =>
        Math.Abs(real.Red - esperada.Red) <= tolerancia && Math.Abs(real.Green - esperada.Green) <= tolerancia
        && Math.Abs(real.Blue - esperada.Blue) <= tolerancia;

    public static bool Contem(byte[] conteudo, string texto) =>
        conteudo.AsSpan().IndexOf(Encoding.ASCII.GetBytes(texto)) >= 0;

    // multipart/form-data como o navegador envia (FormData), no campo "arquivo".
    public static MultipartFormDataContent Multipart(byte[] arquivo, string nomeArquivo = "foto.jpg", string contentType = "image/jpeg",
        string campo = "arquivo")
    {
        var conteudo = new MultipartFormDataContent();
        var parte = new ByteArrayContent(arquivo);
        parte.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        conteudo.Add(parte, campo, nomeArquivo);
        return conteudo;
    }
}
