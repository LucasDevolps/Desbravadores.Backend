using System.Net;
using System.Text;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using Microsoft.Extensions.Logging.Abstractions;

namespace Almirante.Api.Tests;

public sealed class ViaCepClientTests
{
    private static ViaCepClient Cliente(ViaCepFalso handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://viacep.teste/ws/"), Timeout = timeout ?? TimeSpan.FromSeconds(5) },
            NullLogger<ViaCepClient>.Instance);

    private static ResultadoConsultaCep Interpretar(string json) => ViaCepClient.Interpretar("04382160", Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData(" 04382-160 ", "04382160")]
    [InlineData("04382160", "04382160")]
    [InlineData("00000-001", "00000001")]
    [InlineData("4382-160", "4382-160")]
    [InlineData("04382--160", "04382--160")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Cep_Normalizar_SoRemoveEspacosEOHifenDaMascara(string? entrada, string? esperado) =>
        Assert.Equal(esperado, Cep.Normalizar(entrada));

    [Theory]
    [InlineData("04382160", true)]
    [InlineData("00000000", true)]
    [InlineData("4382160", false)]
    [InlineData("043821600", false)]
    [InlineData("0438216a", false)]
    [InlineData("٠٤٣٨٢١٦٠", false)]
    [InlineData(null, false)]
    public void Cep_EhValido_ExigeOitoDigitosAscii(string? cep, bool valido) => Assert.Equal(valido, Cep.EhValido(cep));

    [Fact]
    public async Task Consultar_UsaARotaJsonDoViaCep_EConverteACompleta()
    {
        var handler = new ViaCepFalso();
        var resultado = await Cliente(handler).ConsultarAsync("04382160", CancellationToken.None);

        Assert.Equal(StatusConsultaCep.Encontrado, resultado.Status);
        Assert.Equal(new[] { "04382160" }, handler.Consultas);
        var e = resultado.Endereco!;
        Assert.Equal(("04382160", "Rua Fictícia 04382160", "lado ímpar", null, "Jardim de Teste", "São Paulo", "SP"),
            (e.Cep, e.Logradouro, e.Complemento, e.Unidade, e.Bairro, e.Localidade, e.Uf));
        Assert.Equal(("São Paulo", "Sudeste", "3550308", "1004", "11", "7107"), (e.Estado, e.Regiao, e.Ibge, e.Gia, e.Ddd, e.Siafi));
    }

    [Fact]
    public async Task Consultar_CepNaoNormalizado_ELancaAntesDeChamarARede()
    {
        var handler = new ViaCepFalso();
        await Assert.ThrowsAsync<ArgumentException>(() => Cliente(handler).ConsultarAsync("04382-160", CancellationToken.None));
        Assert.Empty(handler.Consultas);
    }

    [Fact]
    public async Task Consultar_CancelamentoDoChamador_EPropagado_NaoViraTempoEsgotado()
    {
        var handler = new ViaCepFalso { Responder = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ViaCepFalso.Json("{}"); } };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cliente(handler).ConsultarAsync("04382160", cts.Token));
    }

    [Fact]
    public async Task Consultar_TimeoutDoHttpClient_ViraTempoEsgotado()
    {
        var handler = new ViaCepFalso { Responder = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ViaCepFalso.Json("{}"); } };
        var resultado = await Cliente(handler, TimeSpan.FromMilliseconds(100)).ConsultarAsync("04382160", CancellationToken.None);
        Assert.Equal(StatusConsultaCep.TempoEsgotado, resultado.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, StatusConsultaCep.Indisponivel)]
    [InlineData(HttpStatusCode.BadGateway, StatusConsultaCep.Indisponivel)]
    [InlineData(HttpStatusCode.BadRequest, StatusConsultaCep.RespostaInvalida)]
    public async Task Consultar_StatusHttpDeErro(HttpStatusCode status, StatusConsultaCep esperado)
    {
        var handler = new ViaCepFalso { Responder = (_, _) => Task.FromResult(ViaCepFalso.Json("{}", status)) };
        Assert.Equal(esperado, (await Cliente(handler).ConsultarAsync("04382160", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Consultar_ConteudoComContentLengthAcimaDoLimite_EDescartado()
    {
        var handler = new ViaCepFalso
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[ViaCepClient.TamanhoMaximoResposta + 1]),
            }),
        };
        Assert.Equal(StatusConsultaCep.RespostaInvalida, (await Cliente(handler).ConsultarAsync("04382160", CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("""{ "erro": true }""", StatusConsultaCep.NaoEncontrado)]
    [InlineData("""{ "erro": "true" }""", StatusConsultaCep.NaoEncontrado)]
    [InlineData("""{ "erro": "TRUE" }""", StatusConsultaCep.NaoEncontrado)]
    [InlineData("""{ "erro": false }""", StatusConsultaCep.RespostaInvalida)]
    [InlineData("", StatusConsultaCep.RespostaInvalida)]
    [InlineData("null", StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SP" """, StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-161", "localidade": "São Paulo", "uf": "SP" }""", StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-160", "localidade": "", "uf": "SP" }""", StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SPA" }""", StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SP", "gia": "12345678901" }""", StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SP", "bairro": ["x"] }""", StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SP", "bairro": "Vila 🏠" }""", StatusConsultaCep.RespostaInvalida)]
    [InlineData("""{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SP" }""", StatusConsultaCep.Encontrado)]
    [InlineData("""{ "cep": "04382160", "localidade": "São Paulo", "uf": "SP", "bairro": null, "gia": "" }""", StatusConsultaCep.Encontrado)]
    public void Interpretar_SegueOContratoDoViaCep(string json, StatusConsultaCep esperado) => Assert.Equal(esperado, Interpretar(json).Status);

    [Fact]
    public void Interpretar_CamposAusentesOuVazios_FicamNull_SemInventarConteudo()
    {
        var e = Interpretar("""{ "cep": "04382-160", "logradouro": " ", "localidade": " São Paulo ", "uf": "SP", "ibge": "0350308" }""").Endereco!;
        Assert.Equal(("04382160", null, null, null, null, "São Paulo", "SP"), (e.Cep, e.Logradouro, e.Complemento, e.Unidade, e.Bairro, e.Localidade, e.Uf));
        Assert.Equal((null, null, "0350308", null, null, null), (e.Estado, e.Regiao, e.Ibge, e.Gia, e.Ddd, e.Siafi));
    }

    [Fact]
    public void Options_ValidamUrlHttpsEPrazo()
    {
        var validador = new Almirante.Api.Options.ViaCepOptionsValidator();
        Assert.True(validador.Validate(null, new Almirante.Api.Options.ViaCepOptions()).Succeeded);
        Assert.True(validador.Validate(null, new() { BaseUrl = "http://viacep.com.br/ws/" }).Failed);
        Assert.True(validador.Validate(null, new() { BaseUrl = "https://viacep.com.br/ws" }).Failed);
        Assert.True(validador.Validate(null, new() { TimeoutMs = 100 }).Failed);
        Assert.True(validador.Validate(null, new() { TimeoutMs = 30001 }).Failed);
    }
}
