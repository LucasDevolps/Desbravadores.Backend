using System.Text;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using static Almirante.Api.Security.SanitizadorAuditoriaHttp;

namespace Almirante.Api.Tests;

/// <summary>Cópia dos corpos e pontos de borda da política (issue #158): o cliente recebe tudo, a cópia respeita o limite.</summary>
public sealed class AuditoriaHttpCapturaTests
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("""{"status":"Pago","valor":10}""");

    [Fact]
    public async Task LeituraAuditada_RepassaTudoNasQuatroFormasDeLeitura_ECopiaAteOFim()
    {
        var captura = new CapturaCorpoHttp(1024, null);
        await using var leitura = new LeituraAuditadaStream(new MemoryStream(Json), captura);
        var lido = new byte[Json.Length];

        var a = leitura.Read(lido, 0, 4);
        var b = leitura.Read(lido.AsSpan(4, 4));
        var c = await leitura.ReadAsync(lido, 8, 4);
        var d = await leitura.ReadAsync(lido.AsMemory(12));
        var fim = await leitura.ReadAsync(new byte[8]);

        Assert.Equal(Json.Length, a + b + c + d);
        Assert.Equal(0, fim);
        Assert.True(leitura.FimAtingido);
        Assert.Equal(Json, lido);
        Assert.Equal(Json, captura.Conteudo.ToArray());
        Assert.Equal(Json.Length, captura.Bytes);
        Assert.False(leitura.CanSeek || leitura.CanWrite);
        Assert.Throws<NotSupportedException>(() => leitura.Length);
        Assert.Throws<NotSupportedException>(() => leitura.Write([1], 0, 1));
    }

    [Fact]
    public void Captura_AcimaDoLimite_DescartaACopia_MasContaOsBytes()
    {
        var captura = new CapturaCorpoHttp(8, null);

        captura.Observar(Json);

        Assert.True(captura.Excedeu);
        Assert.True(captura.Conteudo.IsEmpty);
        Assert.Equal(Json.Length, captura.Bytes);
    }

    [Fact]
    public async Task EscritaAuditada_ClienteRecebeTudo_PorStreamEPorPipeWriter_ECopiaSoQuandoDecidido()
    {
        var destino = new MemoryStream();
        var interno = new StreamResponseBodyFeature(destino);
        var decisoes = 0;
        var escrita = new EscritaAuditadaStream(interno, () => { decisoes++; return new CapturaCorpoHttp(1024, null); });
        IHttpResponseBodyFeature feature = escrita;

        escrita.Write(Json, 0, 5);
        escrita.Write(Json.AsSpan(5, 5));
        await escrita.WriteAsync(Json, 10, 5);
        await feature.Writer.WriteAsync(Json.AsMemory(15));
        await feature.Writer.FlushAsync();
        escrita.Flush();
        await escrita.FlushAsync();
        await escrita.FinalizarAsync();

        Assert.Equal(Json, destino.ToArray());
        Assert.Equal(Json, escrita.Captura!.Conteudo.ToArray());
        Assert.Equal(1, decisoes);
        Assert.Same(escrita, feature.Stream);
        Assert.False(escrita.CanRead || escrita.CanSeek);
        Assert.Throws<NotSupportedException>(() => escrita.Read(new byte[1], 0, 1));
    }

    [Fact]
    public async Task EscritaAuditada_SendFile_SegueOCaminhoOriginal_EContaSoOTamanho()
    {
        var arquivo = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(arquivo, Json);
            var destino = new MemoryStream();
            var escrita = new EscritaAuditadaStream(new StreamResponseBodyFeature(destino), () => new CapturaCorpoHttp(1024, EstadoCapturaHttp.OmitidaPorTipo));

            await escrita.StartAsync();
            await escrita.SendFileAsync(arquivo, 2, null);
            escrita.DisableBuffering();
            await escrita.CompleteAsync();

            Assert.Equal(Json[2..], destino.ToArray());
            Assert.True(escrita.UsouSendFile);
            Assert.Equal(Json.Length - 2, escrita.Captura!.Bytes);
            Assert.True(escrita.Captura.Conteudo.IsEmpty);
        }
        finally
        {
            File.Delete(arquivo);
        }
    }

    [Theory]
    [InlineData("/api/Usuarios?cpf=52998224725&page=2", "/api/Usuarios?cpf=[omitido]&page=2")]
    [InlineData("/api/NaoExiste", "/api/[omitido]")]
    [InlineData("//atacante.exemplo/x", "[omitido]")]
    [InlineData("ftp://servidor/api", "[omitido]")]
    [InlineData("relativa/sem/barra", "[omitido]")]
    [InlineData("http://[::1]:8080/api/Usuarios", "http://[::1]:8080/api/Usuarios")]
    public void UrlAvulsa_RelativaOuAbsoluta(string valor, string esperado) =>
        Assert.Equal(esperado, SanitizarUrlAvulsa(valor, s => s is "api" or "Usuarios"));

    [Theory]
    [InlineData("api.exemplo.com.br:443", "api.exemplo.com.br:443")]
    [InlineData("[2001:db8::1]:8443", "[2001:db8::1]:8443")]
    [InlineData("maria@exemplo.com", "[host omitido]")]
    [InlineData("host com espaço", "[host omitido]")]
    public void Host_SoNomeOuIp(string host, string esperado) => Assert.Equal(esperado, SanitizarHost(new HostString(host)));

    [Fact]
    public void Esquema_SoHttpEHttps()
    {
        Assert.Equal("https", SanitizarEsquema("https"));
        Assert.Equal("[omitido]", SanitizarEsquema("javascript"));
        Assert.Equal(string.Empty, SanitizarQuery(QueryString.Empty));
        Assert.Equal(string.Empty, SanitizarCaminho(null, null, null, _ => false));
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("text/json", true)]
    [InlineData("application/xml", false)]
    [InlineData("multipart/form-data; boundary=x", false)]
    [InlineData("não é tipo", false)]
    [InlineData(null, false)]
    public void SoJsonEhSanitizado(string? tipo, bool json) => Assert.Equal(json, EhJson(tipo));

    [Fact]
    public void DiretorioPadrao_SeparadoPorServidorEBanco_EEstavel()
    {
        var a = AuditoriaHttpOptions.DiretorioPadrao("Server=sql1;Database=almirante;User Id=x;Password=y");
        var aOutraCredencial = AuditoriaHttpOptions.DiretorioPadrao("Server=SQL1;Database=ALMIRANTE;User Id=z;Password=w");
        var b = AuditoriaHttpOptions.DiretorioPadrao("Server=sql1;Database=outro");

        Assert.Equal(a, aOutraCredencial);
        Assert.NotEqual(a, b);
        Assert.StartsWith(Path.GetTempPath(), a);
        Assert.NotEqual(a, AuditoriaHttpOptions.DiretorioPadrao(null));
        Assert.NotEqual(a, AuditoriaHttpOptions.DiretorioPadrao("isto=não;é válido=para o sql client"));
        Assert.Equal(256L * 1024 * 1024, new AuditoriaHttpOptions().LimiteDiarioBytes);
    }

    [Fact]
    public void Corpo_MarcadoresEstruturados()
    {
        Assert.Equal("""{"semCorpo":true}""", SemCorpo);
        Assert.Equal("""{"omitido":"x","tipoConteudo":null,"tamanhoBytes":null}""", MarcadorCorpo("x", null, null));
        Assert.Equal("""{"omitido":"x","tipoConteudo":"image/webp","tamanhoBytes":3}""", MarcadorCorpo("x", "image/webp", 3));
    }
}
