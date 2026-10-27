namespace Almirante.Api.Entities;

/// <summary>O que a auditoria HTTP conseguiu guardar do corpo de uma direção (requisição ou resposta) e, quando não guardou, por quê.</summary>
public enum EstadoCapturaHttp
{
    /// <summary>Corpo JSON lido por inteiro, sanitizado e gravado dentro do limite.</summary>
    Completa = 1,

    /// <summary>Não havia corpo (GET sem conteúdo, 204, 304...).</summary>
    SemCorpo = 2,

    /// <summary>Rota de credenciais (/api/Auth, [ConteudoSensivel]): só metadados, nunca o conteúdo.</summary>
    OmitidaPorSeguranca = 3,

    /// <summary>Tipo sem sanitização garantida (multipart, imagem, texto, binário, comprimido, WebSocket): só tipo e tamanho.</summary>
    OmitidaPorTipo = 4,

    /// <summary>Corpo maior que o limite de captura: sem o documento inteiro não há como sanitizar, então só tipo e tamanho.</summary>
    OmitidaPorTamanho = 5,

    /// <summary>Sanitizado por inteiro e depois cortado no limite de gravação (o trecho guardado já está sanitizado).</summary>
    Truncada = 6,

    /// <summary>Cliente desconectou ou a resposta foi abortada antes de terminar.</summary>
    Interrompida = 7,

    /// <summary>JSON inválido ou falha da própria sanitização: conteúdo omitido, nunca gravado bruto.</summary>
    FalhaSanitizacao = 8,

    /// <summary>A API respondeu sem ler o corpo da requisição (ex.: 401, 403, 429 antes do model binding).</summary>
    NaoLida = 9,
}

/// <summary>
/// Uma chamada HTTP que chegou ao pipeline da API (issue #158), já sanitizada antes de sair da memória da requisição: sem login,
/// credenciais, CPF, e-mail, telefone, dados de responsáveis, saúde, endereço ou arquivos. Só inserção (trigger recusa UPDATE e
/// DELETE e a identidade de runtime só tem SELECT e INSERT); o Id é gerado na captura e deduplica as novas tentativas.
/// </summary>
public sealed class AuditoriaHttp
{
    public const string Anonimo = "ANONIMO";
    public const string IpIndisponivel = "INDISPONIVEL";
    public const int UrlMaximo = 2048;
    public const int MetodoMaximo = 16;

    public Guid Id { get; set; }

    /// <summary>Esquema, host, porta, path base, caminho e query, com valores sensíveis ou não revisados trocados por marcador.</summary>
    public required string Url { get; set; }

    public required string MetodoHttp { get; set; }

    /// <summary>JSON: conteúdo sanitizado ou marcador estruturado (sem corpo, omitido e o motivo).</summary>
    public required string Request { get; set; }

    /// <summary>JSON sanitizado ou marcador; nulo quando não houve corpo ou nenhuma resposta chegou a ser produzida.</summary>
    public string? Response { get; set; }

    /// <summary>Status efetivamente produzido; nulo quando a resposta não começou (ex.: cliente desconectou antes).</summary>
    public int? StatusCode { get; set; }

    /// <summary>JSON {"request": {...}, "response": {...}} só com os cabeçalhos da lista permitida; credenciais viram marcador.</summary>
    public required string Headers { get; set; }

    /// <summary>JSON com tipo, código e pilha técnica da exceção, sem mensagem; nulo sem exceção.</summary>
    public string? Exception { get; set; }

    public DateTimeOffset DataRequestUtc { get; set; }
    public int DuracaoMs { get; set; }

    /// <summary>IP observado depois dos proxies confiáveis (UseForwardedHeaders) ou <see cref="IpIndisponivel"/>.</summary>
    public required string IpUsuario { get; set; }

    /// <summary>Id interno do token validado (sub) ou <see cref="Anonimo"/>; nunca o e-mail nem algo informado pelo cliente.</summary>
    public required string IdentificacaoUsuario { get; set; }

    public required string TraceId { get; set; }

    public EstadoCapturaHttp EstadoCapturaRequest { get; set; }
    public EstadoCapturaHttp EstadoCapturaResponse { get; set; }
}
