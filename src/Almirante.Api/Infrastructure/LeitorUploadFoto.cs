using System.Buffers;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Almirante.Api.Infrastructure;

public sealed record UploadFoto(byte[] Conteudo, FormatoFoto Formato);

// Leitura do multipart do envio de foto (issue #116) em streaming, sem IFormFile: o original nunca vai para o disco (nem
// como temporário do ASP.NET) e o corpo é lido no máximo até o limite configurado + 1 byte. Ordem: compressão de
// transporte, tipo multipart, Content-Length declarado, limite do servidor para o corpo, uma única parte "arquivo" com
// extensão e Content-Type permitidos e coerentes. A assinatura real e a imagem em si são validadas em ProcessadorFotos.
public static class LeitorUploadFoto
{
    public const string Campo = "arquivo";

    // Boundary, Content-Disposition e Content-Type da parte, além do arquivo.
    public const int MargemMultipartBytes = 16 * 1024;

    private const int MaximoBoundary = 70; // RFC 2046

    private static readonly Dictionary<string, FormatoFoto> PorExtensao = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = FormatoFoto.Jpeg,
        [".jpeg"] = FormatoFoto.Jpeg,
        [".png"] = FormatoFoto.Png,
        [".webp"] = FormatoFoto.Webp,
    };

    private static readonly Dictionary<string, FormatoFoto> PorContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = FormatoFoto.Jpeg,
        ["image/png"] = FormatoFoto.Png,
        ["image/webp"] = FormatoFoto.Webp,
    };

    public static async Task<UploadFoto> LerAsync(HttpRequest request, long limiteArquivo, CancellationToken ct)
    {
        // Zip bomb no transporte: a API nunca descomprime corpo de requisição (não há UseRequestDecompression). Um corpo
        // comprimido é recusado em vez de ser lido como se fosse a imagem (RFC 7694: 415).
        if (request.Headers.ContentEncoding.Count > 0)
        {
            throw NaoSuportado("Content-Encoding não suportado.", "Envie a foto sem compressão de transporte (sem o header Content-Encoding).");
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var tipo)
            || !tipo.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw NaoSuportado("Formato de envio não suportado.", $"Envie a foto como multipart/form-data, no campo '{Campo}'.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(tipo.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaximoBoundary)
        {
            throw Invalido("Boundary do multipart ausente ou inválido.");
        }

        var limiteCorpo = limiteArquivo + MargemMultipartBytes;
        if (request.ContentLength > limiteCorpo)
        {
            throw MuitoGrande(limiteArquivo);
        }

        // Kestrel e IIS cortam o corpo (413) assim que passar do limite, mesmo sem Content-Length (chunked).
        var tamanhoMaximo = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (tamanhoMaximo is { IsReadOnly: false })
        {
            tamanhoMaximo.MaxRequestBodySize = limiteCorpo;
        }

        try
        {
            var leitor = new MultipartReader(boundary, request.Body) { HeadersCountLimit = 4, HeadersLengthLimit = 2048 };
            var secao = await leitor.ReadNextSectionAsync(ct) ?? throw Invalido($"Nenhum arquivo enviado: use o campo '{Campo}'.");
            var formato = ValidarParte(secao);
            var conteudo = await LerConteudoAsync(secao.Body, limiteArquivo, ct);
            if (await leitor.ReadNextSectionAsync(ct) is not null)
            {
                throw Invalido($"Envie um único arquivo, no campo '{Campo}'.");
            }

            return new UploadFoto(conteudo, formato);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw MuitoGrande(limiteArquivo);
        }
        catch (InvalidDataException)
        {
            throw Invalido("Corpo multipart malformado.");
        }
        catch (IOException)
        {
            // Fim inesperado do corpo (multipart truncado ou conexão interrompida); BadHttpRequestException também é IOException.
            throw Invalido("Corpo multipart incompleto.");
        }
    }

    private static FormatoFoto ValidarParte(MultipartSection secao)
    {
        if (!ContentDispositionHeaderValue.TryParse(secao.ContentDisposition, out var disposicao)
            || !disposicao.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
            || !HeaderUtilities.RemoveQuotes(disposicao.Name).Equals(Campo, StringComparison.Ordinal))
        {
            throw Invalido($"Envie a foto no campo '{Campo}' do multipart/form-data.");
        }

        // O nome do arquivo só serve para a allowlist de extensão: nunca vira nome físico nem vai para o log.
        var nome = HeaderUtilities.RemoveQuotes(disposicao.FileNameStar.HasValue ? disposicao.FileNameStar : disposicao.FileName).Value;
        if (string.IsNullOrWhiteSpace(nome))
        {
            throw Invalido($"O campo '{Campo}' precisa ser um arquivo (filename ausente).");
        }

        if (!PorExtensao.TryGetValue(Path.GetExtension(nome), out var porExtensao))
        {
            throw NaoSuportado("Extensão não permitida.", "Envie um arquivo .jpg, .jpeg, .png ou .webp.");
        }

        if (!MediaTypeHeaderValue.TryParse(secao.ContentType, out var tipoArquivo) || !PorContentType.TryGetValue(tipoArquivo.MediaType.Value!, out var porTipo))
        {
            throw NaoSuportado("Content-Type do arquivo não permitido.", "Envie o arquivo como image/jpeg, image/png ou image/webp.");
        }

        if (porExtensao != porTipo)
        {
            throw NaoSuportado("Extensão e Content-Type divergentes.", "A extensão do arquivo não corresponde ao Content-Type informado.");
        }

        return porTipo;
    }

    private static async Task<byte[]> LerConteudoAsync(Stream corpo, long limite, CancellationToken ct)
    {
        using var destino = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int lidos;
            while ((lidos = await corpo.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                if (destino.Length + lidos > limite)
                {
                    throw MuitoGrande(limite);
                }

                destino.Write(buffer, 0, lidos);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return destino.Length > 0 ? destino.ToArray() : throw Invalido("O arquivo enviado está vazio.");
    }

    private static ApiProblemException Invalido(string detalhe) =>
        new(StatusCodes.Status400BadRequest, "Envio de foto inválido.", detalhe);

    private static ApiProblemException NaoSuportado(string titulo, string detalhe) =>
        new(StatusCodes.Status415UnsupportedMediaType, titulo, detalhe);

    private static ApiProblemException MuitoGrande(long limite) =>
        new(StatusCodes.Status413PayloadTooLarge, "Foto grande demais.", $"A foto deve ter no máximo {limite / (1024 * 1024)} MB.");
}
