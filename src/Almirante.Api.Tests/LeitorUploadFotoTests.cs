using System.Text;
using Almirante.Api.Infrastructure;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Almirante.Api.Tests;

// Leitura do multipart do envio de foto (issue #116) direto no LeitorUploadFoto, com corpo montado byte a byte.
public sealed class LeitorUploadFotoTests
{
    private const string Boundary = "----limite-de-teste";
    private const long Limite = 1024;

    private sealed class LimiteDoCorpo : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }
        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }

    // Corpo que falha na leitura como o Kestrel/IIS falham ao estourar o limite (ou com outro erro de requisição).
    private sealed class CorpoQueFalha(Exception erro) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw erro;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw erro;
    }

    private static byte[] Parte(byte[] arquivo, string disposicao = "form-data; name=\"arquivo\"; filename=\"foto.jpg\"",
        string? contentType = "image/jpeg", bool fechar = true, string? segundaParte = null)
    {
        var texto = new StringBuilder($"--{Boundary}\r\n");
        if (disposicao.Length > 0) texto.Append($"Content-Disposition: {disposicao}\r\n");
        if (contentType is not null) texto.Append($"Content-Type: {contentType}\r\n");
        texto.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(texto.ToString()).Concat(arquivo).Concat(Encoding.ASCII.GetBytes("\r\n")).ToList();
        if (segundaParte is not null) bytes.AddRange(Encoding.ASCII.GetBytes($"--{Boundary}\r\n{segundaParte}\r\n\r\nvalor\r\n"));
        if (fechar) bytes.AddRange(Encoding.ASCII.GetBytes($"--{Boundary}--\r\n"));
        return [.. bytes];
    }

    private static HttpRequest Requisicao(byte[] corpo, string? contentType = $"multipart/form-data; boundary={Boundary}",
        IHttpMaxRequestBodySizeFeature? limite = null, Stream? stream = null, long? contentLength = -1)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.ContentType = contentType;
        contexto.Request.Body = stream ?? new MemoryStream(corpo);
        contexto.Request.ContentLength = contentLength == -1 ? corpo.Length : contentLength;
        if (limite is not null) contexto.Features.Set(limite);
        return contexto.Request;
    }

    private static async Task<ApiProblemException> RecusaAsync(HttpRequest requisicao, long limite = Limite) =>
        await Assert.ThrowsAsync<ApiProblemException>(() => LeitorUploadFoto.LerAsync(requisicao, limite, CancellationToken.None));

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    // ------------------------------------------------------------------ aceito

    [Fact]
    public async Task ParteUnicaValida_DevolveOConteudoEOFormato_EAjustaOLimiteDoServidor()
    {
        var limite = new LimiteDoCorpo();

        var upload = await LeitorUploadFoto.LerAsync(Requisicao(Parte(Jpeg), limite: limite), Limite, CancellationToken.None);

        Assert.Equal(Jpeg, upload.Conteudo);
        Assert.Equal(FormatoFoto.Jpeg, upload.Formato);
        Assert.Equal(Limite + LeitorUploadFoto.MargemMultipartBytes, limite.MaxRequestBodySize);
    }

    [Theory]
    [InlineData("form-data; name=\"arquivo\"; filename*=UTF-8''foto%20%C3%A9.PNG", "image/png", FormatoFoto.Png)]
    [InlineData("form-data; name=arquivo; filename=perfil.webp", "image/webp", FormatoFoto.Webp)]
    [InlineData("form-data; name=\"arquivo\"; filename=\"x.jpeg\"", "IMAGE/JPEG", FormatoFoto.Jpeg)]
    public async Task VariacoesValidasDoCabecalhoDaParte(string disposicao, string tipo, FormatoFoto esperado)
    {
        var upload = await LeitorUploadFoto.LerAsync(Requisicao(Parte(Jpeg, disposicao, tipo)), Limite, CancellationToken.None);

        Assert.Equal(esperado, upload.Formato);
    }

    [Fact]
    public async Task LimiteDoServidorSomenteLeitura_OuSemContentLength_NaoAtrapalha()
    {
        var somenteLeitura = new LimiteDoCorpo { IsReadOnly = true };

        await LeitorUploadFoto.LerAsync(Requisicao(Parte(Jpeg), limite: somenteLeitura, contentLength: null), Limite, CancellationToken.None);

        Assert.Equal(30_000_000, somenteLeitura.MaxRequestBodySize);
    }

    // ------------------------------------------------------------------ transporte e tipo do envio

    [Fact]
    public async Task ContentEncoding_EhRecusadoComo415_ZipBombNoTransporte()
    {
        var requisicao = Requisicao(Parte(Jpeg));
        requisicao.Headers.ContentEncoding = "gzip";

        var recusa = await RecusaAsync(requisicao);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, recusa.Status);
        Assert.Contains("Content-Encoding", recusa.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("image/jpeg")]
    public async Task NaoMultipart_Retorna415(string? contentType)
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg), contentType));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, recusa.Status);
    }

    [Theory]
    [InlineData("multipart/form-data")]
    [InlineData("multipart/form-data; boundary=\"\"")]
    [InlineData("multipart/form-data; boundary=0123456789012345678901234567890123456789012345678901234567890123456789X")]
    public async Task BoundaryAusenteOuLongoDemais_Retorna400(string contentType)
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg), contentType));

        Assert.Equal(StatusCodes.Status400BadRequest, recusa.Status);
        Assert.Contains("Boundary", recusa.Detail);
    }

    [Fact]
    public async Task ContentLengthAcimaDoLimite_Retorna413_SemLerOCorpo()
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg), contentLength: Limite + LeitorUploadFoto.MargemMultipartBytes + 1,
            stream: new CorpoQueFalha(new InvalidOperationException("o corpo não deveria ser lido"))));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, recusa.Status);
    }

    [Fact]
    public async Task ServidorCortaOCorpoPorTamanho_Retorna413()
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg), stream: new CorpoQueFalha(new BadHttpRequestException("grande", StatusCodes.Status413PayloadTooLarge))));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, recusa.Status);
    }

    [Fact]
    public async Task OutroErroDeRequisicao_OuCorpoTruncado_Retorna400()
    {
        var erroDeRequisicao = await RecusaAsync(Requisicao(Parte(Jpeg), stream: new CorpoQueFalha(new BadHttpRequestException("ruim", StatusCodes.Status400BadRequest))));
        var truncado = await RecusaAsync(Requisicao(Parte(Jpeg, fechar: false)[..^10]));

        Assert.Equal(StatusCodes.Status400BadRequest, erroDeRequisicao.Status);
        Assert.Contains("incompleto", erroDeRequisicao.Detail);
        Assert.Equal(StatusCodes.Status400BadRequest, truncado.Status);
    }

    [Fact]
    public async Task CabecalhoDaParteGrandeDemais_Retorna400Malformado()
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg, contentType: "image/jpeg; x=" + new string('a', 3000))));

        Assert.Equal(StatusCodes.Status400BadRequest, recusa.Status);
        Assert.Contains("malformado", recusa.Detail);
    }

    // ------------------------------------------------------------------ a parte

    [Fact]
    public async Task SemNenhumaParte_Retorna400()
    {
        var recusa = await RecusaAsync(Requisicao(Encoding.ASCII.GetBytes($"--{Boundary}--\r\n")));

        Assert.Equal(StatusCodes.Status400BadRequest, recusa.Status);
        Assert.Contains("Nenhum arquivo", recusa.Detail);
    }

    [Theory]
    [InlineData("")]                                                         // sem Content-Disposition
    [InlineData("attachment; name=\"arquivo\"; filename=\"foto.jpg\"")]       // não é form-data
    [InlineData("form-data; name=\"foto\"; filename=\"foto.jpg\"")]           // campo errado
    [InlineData("form-data; name=\"arquivo\"")]                               // não é arquivo
    public async Task ParteSemCampoArquivo_Retorna400(string disposicao)
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg, disposicao)));

        Assert.Equal(StatusCodes.Status400BadRequest, recusa.Status);
    }

    [Theory]
    [InlineData("form-data; name=\"arquivo\"; filename=\"foto.gif\"", "image/jpeg", "Extensão")]
    [InlineData("form-data; name=\"arquivo\"; filename=\"foto.svg\"", "image/svg+xml", "Extensão")]
    [InlineData("form-data; name=\"arquivo\"; filename=\"foto\"", "image/jpeg", "Extensão")]
    [InlineData("form-data; name=\"arquivo\"; filename=\"foto.jpg\"", null, "Content-Type")]
    [InlineData("form-data; name=\"arquivo\"; filename=\"foto.jpg\"", "image/gif", "Content-Type")]
    [InlineData("form-data; name=\"arquivo\"; filename=\"foto.png\"", "image/jpeg", "divergentes")]
    public async Task ExtensaoOuTipoDaParteForaDaAllowlist_Retorna415(string disposicao, string? tipo, string titulo)
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg, disposicao, tipo)));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, recusa.Status);
        Assert.Contains(titulo, recusa.Title);
    }

    [Fact]
    public async Task ArquivoMaiorQueOLimite_Retorna413_MesmoComContentLengthDentroDaMargem()
    {
        var grande = new byte[Limite + 1];

        var recusa = await RecusaAsync(Requisicao(Parte(grande)));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, recusa.Status);
        Assert.Contains("no máximo 0 MB", recusa.Detail);   // limite de teste (1 KB) arredondado para MB
    }

    [Fact]
    public async Task ArquivoVazio_Retorna400()
    {
        var recusa = await RecusaAsync(Requisicao(Parte([])));

        Assert.Equal(StatusCodes.Status400BadRequest, recusa.Status);
        Assert.Contains("vazio", recusa.Detail);
    }

    [Fact]
    public async Task DuasPartes_Retorna400()
    {
        var recusa = await RecusaAsync(Requisicao(Parte(Jpeg, segundaParte: "Content-Disposition: form-data; name=\"extra\"")));

        Assert.Equal(StatusCodes.Status400BadRequest, recusa.Status);
        Assert.Contains("único arquivo", recusa.Detail);
    }
}
