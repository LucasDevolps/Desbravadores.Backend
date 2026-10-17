using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Infrastructure;
using Almirante.Api.Validation.Usuarios;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Almirante.Api.Services;

public enum StatusConsultaCep
{
    Encontrado,
    NaoEncontrado,
    Indisponivel,
    TempoEsgotado,
    RespostaInvalida,
}

public sealed record ResultadoConsultaCep(StatusConsultaCep Status, CepDto? Endereco = null)
{
    /// <summary>Endereço encontrado ou a recusa da API: 400/404 (CEP inexistente, conforme o uso) ou 503 (ViaCEP fora do ar, lento ou com resposta inválida).</summary>
    public CepDto ExigirEndereco(Func<ApiProblemException> cepInexistente, string consequencia) => Status switch
    {
        StatusConsultaCep.Encontrado => Endereco!,
        StatusConsultaCep.NaoEncontrado => throw cepInexistente(),
        StatusConsultaCep.TempoEsgotado => throw ConsultaIndisponivel($"O ViaCEP não respondeu no prazo. {consequencia}"),
        StatusConsultaCep.RespostaInvalida => throw ConsultaIndisponivel($"O ViaCEP devolveu uma resposta inválida. {consequencia}"),
        _ => throw ConsultaIndisponivel($"O ViaCEP está indisponível. {consequencia}"),
    };

    private static ApiProblemException ConsultaIndisponivel(string detalhe) =>
        new(StatusCodes.Status503ServiceUnavailable, "Consulta de CEP indisponível.", detalhe.Trim());
}

public interface IConsultaCep
{
    /// <summary>cep já normalizado (8 dígitos). Nunca lança por falha do ViaCEP; só propaga o cancelamento do chamador.</summary>
    Task<ResultadoConsultaCep> ConsultarAsync(string cep, CancellationToken ct);
}

/// <summary>
/// Cliente do ViaCEP (https://viacep.com.br/ws/{cep}/json/). Prazo e nova tentativa vêm do pipeline de resiliência configurado no
/// Program (ViaCep:TimeoutMs). O CEP consultado não vai para o log (no cadastro, é o endereço do usuário); por isso o Program
/// também remove o log padrão do HttpClient, que gravaria a URL.
/// </summary>
public sealed class ViaCepClient(HttpClient http, ILogger<ViaCepClient> logger) : IConsultaCep
{
    public const string NomeHttpClient = "ViaCep";
    public const int TamanhoMaximoResposta = 16 * 1024;

    public async Task<ResultadoConsultaCep> ConsultarAsync(string cep, CancellationToken ct)
    {
        if (!Cep.EhValido(cep))
        {
            throw new ArgumentException("CEP deve ter 8 dígitos.", nameof(cep));
        }

        byte[] conteudo;
        try
        {
            using var response = await http.GetAsync($"{cep}/json/", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Consulta ao ViaCEP recusada com HTTP {Status}.", (int)response.StatusCode);
                return new(response.StatusCode == System.Net.HttpStatusCode.BadRequest ? StatusConsultaCep.RespostaInvalida : StatusConsultaCep.Indisponivel);
            }

            var lido = await LerLimitadoAsync(response.Content, ct);
            if (lido is null)
            {
                logger.LogWarning("Resposta do ViaCEP acima de {Limite} bytes descartada.", TamanhoMaximoResposta);
                return new(StatusConsultaCep.RespostaInvalida);
            }

            conteudo = lido;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or OperationCanceledException)
        {
            logger.LogWarning("Consulta ao ViaCEP excedeu o prazo.");
            return new(StatusConsultaCep.TempoEsgotado);
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException or IOException)
        {
            logger.LogWarning("ViaCEP indisponível: {Erro}.", ex.GetType().Name);
            return new(StatusConsultaCep.Indisponivel);
        }

        var resultado = Interpretar(cep, conteudo);
        if (resultado.Status == StatusConsultaCep.RespostaInvalida)
        {
            logger.LogWarning("Resposta do ViaCEP fora do contrato esperado descartada.");
        }

        return resultado;
    }

    /// <summary>Converte o JSON do ViaCEP; qualquer desvio do contrato (campo longo demais, fora da página 1252, código não numérico, CEP trocado) torna a resposta inválida em vez de truncar ou inventar.</summary>
    public static ResultadoConsultaCep Interpretar(string cepConsultado, ReadOnlySpan<byte> json)
    {
        try
        {
            using var documento = JsonDocument.Parse(json.ToArray());
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                return new(StatusConsultaCep.RespostaInvalida);
            }

            if (raiz.TryGetProperty("erro", out var erro))
            {
                var naoEncontrado = erro.ValueKind == JsonValueKind.True
                    || (erro.ValueKind == JsonValueKind.String && string.Equals(erro.GetString(), "true", StringComparison.OrdinalIgnoreCase));
                return new(naoEncontrado ? StatusConsultaCep.NaoEncontrado : StatusConsultaCep.RespostaInvalida);
            }

            var cep = Cep.Normalizar(Texto(raiz, "cep"));
            var localidade = Texto(raiz, "localidade");
            var uf = Texto(raiz, "uf");
            if (cep != cepConsultado || localidade is null || uf is not { Length: Endereco.UfTamanho } || !uf.All(char.IsAsciiLetterUpper))
            {
                return new(StatusConsultaCep.RespostaInvalida);
            }

            var dto = new CepDto
            {
                Cep = cep,
                Logradouro = Texto(raiz, "logradouro"),
                Complemento = Texto(raiz, "complemento"),
                Unidade = Texto(raiz, "unidade"),
                Bairro = Texto(raiz, "bairro"),
                Localidade = localidade,
                Uf = uf,
                Estado = Texto(raiz, "estado"),
                Regiao = Texto(raiz, "regiao"),
                Ibge = Texto(raiz, "ibge"),
                Gia = Texto(raiz, "gia"),
                Ddd = Texto(raiz, "ddd"),
                Siafi = Texto(raiz, "siafi"),
            };

            var valido = CabeTexto(dto.Logradouro, Endereco.LogradouroMaximo) && CabeTexto(dto.Complemento, Endereco.ComplementoCepMaximo)
                && CabeTexto(dto.Unidade, Endereco.UnidadeMaximo) && CabeTexto(dto.Bairro, Endereco.BairroMaximo)
                && CabeTexto(dto.Localidade, Endereco.LocalidadeMaximo) && CabeTexto(dto.Estado, Endereco.EstadoMaximo)
                && CabeTexto(dto.Regiao, Endereco.RegiaoMaximo)
                && Codigo(dto.Ibge, Endereco.IbgeMaximo) && Codigo(dto.Gia, Endereco.GiaMaximo)
                && Codigo(dto.Ddd, Endereco.DddMaximo) && Codigo(dto.Siafi, Endereco.SiafiMaximo);
            return valido ? new(StatusConsultaCep.Encontrado, dto) : new(StatusConsultaCep.RespostaInvalida);
        }
        catch (Exception ex) when (ex is JsonException or ContratoViaCepException)
        {
            return new(StatusConsultaCep.RespostaInvalida);
        }
    }

    private static string? Texto(JsonElement raiz, string campo)
    {
        if (!raiz.TryGetProperty(campo, out var valor) || valor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (valor.ValueKind != JsonValueKind.String)
        {
            throw new ContratoViaCepException();
        }

        var texto = valor.GetString()!.Trim();
        return texto.Length == 0 ? null : texto;
    }

    private static bool CabeTexto(string? valor, int maximo) => valor is null || (valor.Length <= maximo && TextoVarchar.CabeNaPagina1252(valor));

    private static bool Codigo(string? valor, int maximo) => valor is null || (valor.Length <= maximo && valor.All(char.IsAsciiDigit));

    private static async Task<byte[]?> LerLimitadoAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > TamanhoMaximoResposta)
        {
            return null;
        }

        await using var origem = await content.ReadAsStreamAsync(ct);
        using var destino = new MemoryStream();
        var buffer = new byte[4096];
        int lidos;
        while ((lidos = await origem.ReadAsync(buffer, ct)) > 0)
        {
            if (destino.Length + lidos > TamanhoMaximoResposta)
            {
                return null;
            }

            destino.Write(buffer, 0, lidos);
        }

        return destino.ToArray();
    }

    private sealed class ContratoViaCepException : Exception;
}
