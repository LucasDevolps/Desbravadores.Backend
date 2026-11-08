using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Security;

/// <summary>Como a auditoria HTTP trata o valor de um campo, parâmetro de query ou de rota, decidido só pelo nome.</summary>
public enum CategoriaCampoAuditoria
{
    /// <summary>Fora da lista revisada: valor nunca gravado, nem de objetos e listas dentro dele.</summary>
    NaoRevisado,
    Credencial,
    Pessoal,
    /// <summary>Nome terminado em Id/Ids: só GUID, inteiro curto ou identificador de trace.</summary>
    Identificador,
    Numero,
    Booleano,
    Data,
    /// <summary>Valor de enum ou código de catálogo: uma palavra, sem espaço, sem sequência longa de dígitos.</summary>
    Enumeracao,
    Finalidade,
    /// <summary>Texto curto gerado pelo servidor (título de Problem Details, área do catálogo): sem '@' e com poucos dígitos.</summary>
    TextoControlado,
    Url,
    /// <summary>Texto digitado por pessoas (nome, descrição, motivo, busca): só o tamanho.</summary>
    TextoLivre,
    /// <summary>Objeto ou lista: os filhos são classificados pelo próprio nome; valores soltos só se forem número, booleano ou GUID.</summary>
    Estrutura,
    /// <summary>Dicionário errors do Problem Details: chaves (nomes de campos) mantidas, mensagens omitidas.</summary>
    Erros,
}

/// <summary>
/// Política única da auditoria HTTP (issue #158) para URL, query, rota, corpos JSON, cabeçalhos e exceções. Lista explícita de campos
/// revisados (<see cref="Revisados"/>): campo novo ou desconhecido não é gravado até alguém revisá-lo aqui. Credenciais e dados pessoais
/// (CPF, e-mail, telefone, responsáveis, RG/CIN, saúde, endereço, nascimento, fotos e arquivos, inclusive aliases em inglês e em
/// snake_case) vencem qualquer revisão. Mesmo um campo revisado só mantém valores no formato esperado: o nome não basta. Nada aqui
/// copia conteúdo bruto: o que não pode ser verificado vira marcador.
/// </summary>
public static partial class SanitizadorAuditoriaHttp
{
    public const string MarcadorCredencial = "[credencial omitida]";
    public const string MarcadorPessoal = "[dado pessoal omitido]";
    public const string MarcadorNaoRevisado = "[campo não revisado omitido]";
    public const string MarcadorFormato = "[valor fora do formato esperado omitido]";
    public const string MarcadorMensagem = "[mensagem omitida]";
    public const string MarcadorUrl = "[omitido]";
    public const int ProfundidadeMaxima = 32;
    public const int ItensMaximos = 200;
    public const int FramesMaximos = 40;

    /// <summary>ASCII e Latin-1: o JSON gravado cabe em varchar (code page 1252) sem trocar caractere por '?'.</summary>
    public static readonly JavaScriptEncoder Codificador = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Latin1Supplement);

    private static readonly JsonWriterOptions OpcoesEscrita = new() { Encoder = Codificador };

    private static readonly string[] FragmentosCredencial =
    [
        "senha", "password", "passwd", "passphrase", "token", "secret", "segredo", "cookie", "authorization", "authorisation", "csrf",
        "xsrf", "jwt", "bearer", "apikey", "accesskey", "secretkey", "privatekey", "chaveprivada", "connectionstring", "credencial",
        "credential", "hash", "assinatura", "signature",
    ];

    private static readonly HashSet<string> PalavrasCredencial = ["pwd", "otp", "pin", "sid", "jti", "auth"];

    private static readonly string[] FragmentosPessoais =
    [
        "cpf", "mail", "telefone", "celular", "phone", "whatsapp", "contato", "contact", "endereco", "address", "logradouro", "bairro",
        "cep", "zipcode", "postal", "localidade", "complemento", "nascimento", "birth", "passaporte", "passport", "documento", "document",
        "sangue", "sanguineo", "blood", "doenca", "alergia", "allerg", "disease", "saude", "health", "medic", "remedio", "foto", "photo",
        "imagem", "image", "arquivo", "file", "anexo", "attachment", "responsavel", "guardian", "latitude", "longitude",
    ];

    private static readonly HashSet<string> PalavrasPessoais =
        ["rg", "cin", "ssn", "ip", "uf", "ddd", "gia", "ibge", "siafi", "regiao", "unidade", "geo", "rua", "street", "pai", "mae"];

    private static readonly HashSet<string> PalavrasIdentificador = ["id", "ids", "guid", "uuid"];

    /// <summary>
    /// Campos revisados, pelo nome normalizado (sem acento, minúsculo, sem separadores: dataInicial, data_inicial e DataInicial são o
    /// mesmo). Revisar um campo novo = incluí-lo aqui com a categoria do valor; credencial e dado pessoal nunca entram.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, CategoriaCampoAuditoria> Revisados = Montar(new()
    {
        [CategoriaCampoAuditoria.Numero] =
        [
            "valor", "valorpago", "valorpormembro", "valorrestante", "saldo", "entradas", "saidas", "total", "totalpages", "totalitems",
            "page", "pagesize", "quantidade", "quantidadeativos", "quantidademembros", "quantidademinima", "quantidadeparticipantes",
            "naolidas", "ordem", "percentual", "solicitacoes", "idademinimainicio", "idademinimainvestidura", "idadereferencia",
            "niveldificuldade", "requisitoscumpridos", "requisitospendentes", "unidadesobrigatorias", "unidadessatisfeitas",
            "usuariosprocessados", "lancamentoscriados", "seguroobrigatorio", "concluidas", "canceladas", "emandamento",
            "concluidasaguardandoinvestiduraouentrega", "negotiateversion",
        ],
        [CategoriaCampoAuditoria.Booleano] =
        [
            "ativo", "ativar", "criado", "atendido", "confirmada", "ehgratis", "individual", "incluirfinanceiro", "incluirnaopublicadas",
            "incluirinativas", "includeinactive", "naorealizadaanteriormente", "obrigatorio", "publicar", "satisfeito", "podeconcluir",
            "exigeaprovacaoexterna", "aprovacaooficialconfirmada", "permitemodalidadeagrupada", "aplicaratodososmembros",
            "aguardandoinvestiduraouentrega", "investiduraouentregarealizada", "membroativo", "requisitoscompletos",
        ],
        [CategoriaCampoAuditoria.Data] =
        [
            "data", "datainicial", "datafinal", "dataevento", "datapagamento", "datacriacao", "dataatualizacao", "dataconclusao",
            "datacumprimento", "datainicio", "datainvestiduraouentrega", "dataminimacadastro", "vencimento", "de", "ate", "criadaemutc",
            "criadoem", "criadoemutc", "atualizadoemutc", "publicadaemutc", "lidaemutc", "canceladoemutc", "registradoemutc",
            "validadoemutc", "expiraemutc", "expiresatutc", "solicitadaemutc", "ultimasolicitacaoemutc", "autorizacaoexpiraemutc",
            "pagamentoregistradoemutc", "datahorautc", "ultimaatualizacao", "timestamp",
        ],
        [CategoriaCampoAuditoria.Enumeracao] =
        [
            "status", "tipo", "tipofluxo", "categoria", "situacao", "classe", "classeavancadacorrespondente", "classeregularcorrespondente",
            "grupo", "modalidade", "role", "solicitanterole", "origem", "origemhistorica", "metodoverificacao", "ordenarpor", "periodo",
            "codigo", "codigooficial", "edicao", "edicaorevisao", "operacao", "outcome", "tabela", "acao", "funcao", "regra", "criterio",
            "secao",
        ],
        [CategoriaCampoAuditoria.Finalidade] = ["finalidade"],
        [CategoriaCampoAuditoria.TextoControlado] = ["title", "area", "fonteoficial"],
        [CategoriaCampoAuditoria.Url] = ["type", "url"],
        [CategoriaCampoAuditoria.TextoLivre] =
        [
            "nome", "descricao", "motivo", "local", "observacao", "observacoes", "justificativa", "detalhe", "detail", "mensagem",
            "message", "titulo", "membronome", "especialidadenome", "referencia", "referenciaevidencia", "referenciadistintivo",
            "prerequisitostexto", "busca", "search", "motivodescarte", "motivoinativacao", "instrutor", "criadopor", "instance",
            "versao", "cursor", "proximocursor",
        ],
        [CategoriaCampoAuditoria.Estrutura] =
        [
            "items", "lancamentos", "lancamento", "membros", "membro", "evento", "eventos", "notificacao", "notificacoes", "cargo", "cargos",
            "progresso", "progressodetalhado", "requisitos", "requisito", "subitens", "prerequisitos", "prerequisitosconclusao",
            "exigenciaespecialidade", "especialidade", "especialidades", "revisao", "revisaovigente", "revisoes", "classes", "referencias",
            "transporte", "alimentacao", "financeiro", "desbravadores", "proximoseventos", "minhacobranca", "registradopor", "canceladopor",
            "validadopor", "cancelamento", "conclusao", "investidura", "entrega", "evidencias", "validacoesexternas", "requisitosexternos",
            "regrasoficiais", "bloqueios", "historico", "participantes", "vinculo", "vinculos", "pagina", "recuperacoes", "pendentes",
        ],
        [CategoriaCampoAuditoria.Erros] = ["errors"],
    });

    private static readonly HashSet<string> CabecalhosCredencial =
        new(["Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-CSRF-TOKEN", "X-XSRF-TOKEN", "X-Api-Key"], StringComparer.OrdinalIgnoreCase);

    private static readonly (string Nome, Func<string, string?> Valor)[] CabecalhosRequest =
    [
        ("Accept", ListaDoCliente),
        ("Accept-Language", ListaDoCliente),
        ("Content-Type", TipoConteudo),
        ("Content-Length", v => Digitos().IsMatch(v) ? v : null),
        ("Content-Encoding", ListaDoCliente),
        ("Transfer-Encoding", ListaDoCliente),
        ("Upgrade", ListaDoCliente),
        ("Sec-WebSocket-Version", v => Digitos().IsMatch(v) ? v : null),
        ("X-Requested-With", ListaDoCliente),
        ("Idempotency-Key", v => Guid.TryParse(v, out _) ? v : null),
        ("Origin", v => v == "null" || Origem().IsMatch(v) ? v : null),
        ("User-Agent", v => v.Length <= 300 && !v.Contains('@') && !LetrasDeControle().IsMatch(v) && !DigitosLongos().IsMatch(v)
            && !DocumentoFormatado().IsMatch(v) ? v : null),
    ];

    private static readonly (string Nome, Func<string, string?> Valor)[] CabecalhosResponse =
    [
        ("Content-Type", TipoConteudo),
        ("Content-Length", v => Digitos().IsMatch(v) ? v : null),
        ("Content-Encoding", v => Lista().IsMatch(v) ? v : null),
        ("Cache-Control", v => Lista().IsMatch(v) ? v : null),
        ("Retry-After", v => Digitos().IsMatch(v) ? v : null),
        ("Allow", v => Lista().IsMatch(v) ? v : null),
        ("WWW-Authenticate", DesafioAutenticacao),
    ];

    /// <summary>Categoria do valor de um campo, parâmetro de query ou de rota pelo nome.</summary>
    public static CategoriaCampoAuditoria Classificar(string nome)
    {
        var palavras = Palavras(nome);
        if (palavras.Count == 0)
        {
            return CategoriaCampoAuditoria.NaoRevisado;
        }

        var junto = string.Concat(palavras);
        if (FragmentosCredencial.Any(junto.Contains) || palavras.Any(PalavrasCredencial.Contains))
        {
            return CategoriaCampoAuditoria.Credencial;
        }

        // usuarioResponsavelId, membroId, eventoIds: o valor só passa se for GUID ou inteiro curto (ver ManterTexto).
        if (PalavrasIdentificador.Contains(palavras[^1]))
        {
            return CategoriaCampoAuditoria.Identificador;
        }

        if (FragmentosPessoais.Any(junto.Contains) || palavras.Any(PalavrasPessoais.Contains))
        {
            return CategoriaCampoAuditoria.Pessoal;
        }

        return Revisados.TryGetValue(junto, out var categoria) ? categoria : CategoriaCampoAuditoria.NaoRevisado;
    }

    /// <summary>Palavras do nome: camelCase, PascalCase, snake_case e kebab-case, sem acento e em minúsculas.</summary>
    public static List<string> Palavras(string nome)
    {
        var texto = RemoverAcentos(nome);
        var palavras = new List<string>();
        var atual = new StringBuilder();
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (!char.IsAsciiLetterOrDigit(c))
            {
                Fechar();
                continue;
            }

            if (atual.Length > 0)
            {
                var anterior = texto[i - 1];
                var quebra = (char.IsAsciiLetterUpper(c) && (char.IsAsciiLetterLower(anterior) || char.IsAsciiDigit(anterior)))
                    || (char.IsAsciiLetterUpper(c) && char.IsAsciiLetterUpper(anterior) && i + 1 < texto.Length && char.IsAsciiLetterLower(texto[i + 1]))
                    || char.IsAsciiDigit(c) != char.IsAsciiDigit(anterior);
                if (quebra)
                {
                    Fechar();
                }
            }

            atual.Append(char.ToLowerInvariant(c));
        }

        Fechar();
        return palavras;

        void Fechar()
        {
            if (atual.Length > 0)
            {
                palavras.Add(atual.ToString());
                atual.Clear();
            }
        }
    }

    /// <summary>O valor, se a categoria permite guardá-lo; nulo quando ele deve virar marcador.</summary>
    public static string? ManterTexto(CategoriaCampoAuditoria categoria, string valor) => categoria switch
    {
        CategoriaCampoAuditoria.Identificador => Identificador(valor) ? valor : null,
        CategoriaCampoAuditoria.Numero => NumeroTexto().IsMatch(valor) ? valor : null,
        CategoriaCampoAuditoria.Booleano => bool.TryParse(valor, out _) ? valor : null,
        CategoriaCampoAuditoria.Data => DataIso().IsMatch(valor) ? valor : null,
        CategoriaCampoAuditoria.Enumeracao => Codigo().IsMatch(valor) && ContarDigitos(valor) < 6 ? valor : null,
        CategoriaCampoAuditoria.Finalidade => LancamentoFinalidades.Todas.Contains(valor) ? valor : null,
        CategoriaCampoAuditoria.TextoControlado => TextoControlado(valor) ? valor : null,
        CategoriaCampoAuditoria.Url => valor.Length <= 300 && UrlPublica().IsMatch(valor) ? valor : null,
        CategoriaCampoAuditoria.Estrutura or CategoriaCampoAuditoria.Erros => Guid.TryParse(valor, out _) ? valor : null,
        _ => null,
    };

    public static string Marcador(CategoriaCampoAuditoria categoria, string? texto) => categoria switch
    {
        CategoriaCampoAuditoria.Credencial => MarcadorCredencial,
        CategoriaCampoAuditoria.Pessoal => MarcadorPessoal,
        CategoriaCampoAuditoria.NaoRevisado => MarcadorNaoRevisado,
        CategoriaCampoAuditoria.TextoLivre => texto is null ? "[texto omitido]" : $"[texto omitido: {texto.Length} caracteres]",
        _ => MarcadorFormato,
    };

    /// <summary>Sanitiza um documento JSON inteiro (nunca um trecho) e só depois corta no limite de gravação.</summary>
    public static (EstadoCapturaHttp Estado, string Json) SanitizarJson(ReadOnlyMemory<byte> utf8, int limiteCaracteres)
    {
        string texto;
        try
        {
            using var documento = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 256 });
            var saida = new ArrayBufferWriter<byte>();
            using (var escritor = new Utf8JsonWriter(saida, OpcoesEscrita))
            {
                EscreverValor(escritor, documento.RootElement, CategoriaCampoAuditoria.Estrutura, 0);
            }

            texto = Encoding.UTF8.GetString(saida.WrittenSpan);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return (EstadoCapturaHttp.FalhaSanitizacao, MarcadorCorpo("JSON inválido ou não sanitizável", "application/json", utf8.Length));
        }

        if (texto.Length <= limiteCaracteres)
        {
            return (EstadoCapturaHttp.Completa, texto);
        }

        return (EstadoCapturaHttp.Truncada, Objeto(escritor =>
        {
            escritor.WriteBoolean("truncado", true);
            escritor.WriteNumber("tamanhoSanitizado", texto.Length);
            escritor.WriteString("conteudoParcial", texto[..limiteCaracteres]);
        }));
    }

    /// <summary>Marcador estruturado de corpo não gravado: motivo, tipo de mídia (sem parâmetros) e tamanho.</summary>
    public static string MarcadorCorpo(string motivo, string? tipoConteudo, long? tamanhoBytes) => Objeto(escritor =>
    {
        escritor.WriteString("omitido", motivo);
        var tipo = tipoConteudo is null ? null : TipoConteudo(tipoConteudo);
        if (tipo is null) escritor.WriteNull("tipoConteudo"); else escritor.WriteString("tipoConteudo", tipo);
        if (tamanhoBytes is null) escritor.WriteNull("tamanhoBytes"); else escritor.WriteNumber("tamanhoBytes", tamanhoBytes.Value);
    });

    public static string SemCorpo { get; } = Objeto(escritor => escritor.WriteBoolean("semCorpo", true));

    public static bool EhJson(string? tipoConteudo)
    {
        var tipo = tipoConteudo is null ? null : TipoConteudo(tipoConteudo);
        return tipo is not null && (tipo is "application/json" or "text/json"
            || (tipo.StartsWith("application/", StringComparison.Ordinal) && tipo.EndsWith("+json", StringComparison.Ordinal)));
    }

    /// <summary>Só o tipo de mídia, em minúsculas: parâmetros (boundary, charset) ficam de fora.</summary>
    public static string? TipoConteudo(string valor)
    {
        var tipo = valor.Split(';', 2)[0].Trim().ToLowerInvariant();
        return TipoMidia().IsMatch(tipo) ? tipo : null;
    }

    public static string SanitizarEsquema(string esquema) => esquema is "http" or "https" ? esquema : MarcadorUrl;

    public static string SanitizarHost(HostString host) => host.HasValue && Host().IsMatch(host.Value) ? host.Value : "[host omitido]";

    /// <summary>
    /// Caminho segmento a segmento: literal da rota resolvida ou de qualquer rota da API, parâmetro da rota pela categoria do nome
    /// ({id:guid} mantém o GUID, {cep} vira marcador), GUID ou inteiro curto soltos; o resto (inclusive rotas inexistentes) vira marcador.
    /// </summary>
    public static string SanitizarCaminho(string? caminho, RoutePattern? padrao, IReadOnlyDictionary<string, object?>? valoresRota,
        Func<string, bool> literalConhecido)
    {
        if (string.IsNullOrEmpty(caminho))
        {
            return string.Empty;
        }

        var literais = padrao?.PathSegments.SelectMany(s => s.Parts).OfType<RoutePatternLiteralPart>()
            .Select(p => p.Content).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var segmentos = caminho.Split('/');
        for (var i = 0; i < segmentos.Length; i++)
        {
            var segmento = segmentos[i];
            if (segmento.Length == 0 || literais?.Contains(segmento) == true || literalConhecido(segmento))
            {
                continue;
            }

            var parametro = padrao?.Parameters.FirstOrDefault(p =>
                valoresRota is not null && valoresRota.TryGetValue(p.Name, out var valor)
                && string.Equals(Convert.ToString(valor, CultureInfo.InvariantCulture), segmento, StringComparison.Ordinal));
            var mantido = parametro is not null
                ? ManterTexto(Classificar(parametro.Name), segmento)
                : Guid.TryParse(segmento, out _) || InteiroCurto().IsMatch(segmento) ? segmento : null;
            segmentos[i] = mantido is null ? MarcadorUrl : Uri.EscapeDataString(mantido);
        }

        return string.Join('/', segmentos);
    }

    /// <summary>Query na ordem original: nome revisado mantém o valor no formato esperado; o resto vira [omitido].</summary>
    public static string SanitizarQuery(QueryString query)
    {
        if (!query.HasValue)
        {
            return string.Empty;
        }

        var saida = new StringBuilder();
        foreach (var par in new QueryStringEnumerable(query.Value))
        {
            saida.Append(saida.Length == 0 ? '?' : '&');
            var nome = par.DecodeName().ToString();
            if (!ChaveSegura(nome))
            {
                saida.Append(MarcadorUrl).Append('=').Append(MarcadorUrl);
                continue;
            }

            var valor = ManterTexto(Classificar(nome), par.DecodeValue().ToString());
            saida.Append(Uri.EscapeDataString(nome)).Append('=').Append(valor is null ? MarcadorUrl : Uri.EscapeDataString(valor));
        }

        return saida.ToString();
    }

    /// <summary>Location e afins: URL absoluta ou relativa com o mesmo tratamento de caminho e query, sem contexto de rota.</summary>
    public static string SanitizarUrlAvulsa(string valor, Func<string, bool> literalConhecido)
    {
        if (Uri.TryCreate(valor, UriKind.Absolute, out var absoluta) && absoluta.Scheme is "http" or "https")
        {
            var host = absoluta.IsDefaultPort ? absoluta.Host : $"{absoluta.Host}:{absoluta.Port}";
            return $"{absoluta.Scheme}://{SanitizarHost(new HostString(host))}"
                + SanitizarCaminho(Uri.UnescapeDataString(absoluta.AbsolutePath), null, null, literalConhecido)
                + SanitizarQuery(new QueryString(absoluta.Query.Length > 0 ? absoluta.Query : null));
        }

        if (valor.StartsWith('/') && !valor.StartsWith("//", StringComparison.Ordinal))
        {
            var partes = valor.Split('?', 2);
            return SanitizarCaminho(Uri.UnescapeDataString(partes[0]), null, null, literalConhecido)
                + (partes.Length > 1 ? SanitizarQuery(new QueryString("?" + partes[1])) : string.Empty);
        }

        return MarcadorUrl;
    }

    /// <summary>Cabeçalhos da lista permitida; credenciais aparecem só como presença; desconhecidos não são copiados.</summary>
    public static string SanitizarCabecalhos(IHeaderDictionary request, IHeaderDictionary? response, Func<string, bool> literalConhecido) =>
        Objeto(escritor =>
        {
            escritor.WriteStartObject("request");
            EscreverCabecalhos(escritor, request, CabecalhosRequest);
            escritor.WriteEndObject();
            escritor.WriteStartObject("response");
            if (response is not null)
            {
                EscreverCabecalhos(escritor, response, CabecalhosResponse);
                if (response.Location.Count > 0)
                {
                    escritor.WriteString("Location", SanitizarUrlAvulsa(response.Location.ToString(), literalConhecido));
                }
            }

            escritor.WriteEndObject();
        });

    /// <summary>
    /// Tipo, código (SqlException.Number, status do ApiProblemException), HResult e pilha técnica (só tipos e métodos), sem mensagem,
    /// Exception.Data nem texto de exceções internas, que podem conter SQL, valores e dados pessoais.
    /// </summary>
    public static string SanitizarExcecao(Exception excecao, bool tratada) => Objeto(escritor =>
    {
        escritor.WriteBoolean("tratadaPelaApi", tratada);
        EscreverExcecao(escritor, excecao);
        escritor.WriteString("mensagem", MarcadorMensagem);
        escritor.WriteStartArray("pilha");
        foreach (var frame in new StackTrace(excecao, fNeedFileInfo: false).GetFrames().Take(FramesMaximos))
        {
            var metodo = frame.GetMethod();
            if (metodo is not null)
            {
                escritor.WriteStringValue($"{metodo.DeclaringType?.FullName}.{metodo.Name}");
            }
        }

        escritor.WriteEndArray();
        escritor.WriteStartArray("internas");
        var interna = excecao.InnerException;
        for (var nivel = 0; interna is not null && nivel < 5; nivel++, interna = interna.InnerException)
        {
            escritor.WriteStartObject();
            EscreverExcecao(escritor, interna);
            escritor.WriteEndObject();
        }

        escritor.WriteEndArray();
    });

    public static string Objeto(Action<Utf8JsonWriter> conteudo)
    {
        var saida = new ArrayBufferWriter<byte>();
        using (var escritor = new Utf8JsonWriter(saida, OpcoesEscrita))
        {
            escritor.WriteStartObject();
            conteudo(escritor);
            escritor.WriteEndObject();
        }

        return Encoding.UTF8.GetString(saida.WrittenSpan);
    }

    private static void EscreverExcecao(Utf8JsonWriter escritor, Exception excecao)
    {
        escritor.WriteString("tipo", excecao.GetType().FullName);
        escritor.WriteNumber("hresult", excecao.HResult);
        switch (excecao)
        {
            case SqlException sql:
                escritor.WriteNumber("codigo", sql.Number);
                break;
            case DbUpdateException { InnerException: SqlException sqlInterna }:
                escritor.WriteNumber("codigo", sqlInterna.Number);
                break;
            case ApiProblemException problema:
                escritor.WriteNumber("status", problema.Status);
                break;
        }
    }

    private static void EscreverValor(Utf8JsonWriter escritor, JsonElement elemento, CategoriaCampoAuditoria categoria, int profundidade)
    {
        if (profundidade > ProfundidadeMaxima)
        {
            escritor.WriteStringValue("[profundidade excedida]");
            return;
        }

        switch (elemento.ValueKind)
        {
            case JsonValueKind.Null:
                escritor.WriteNullValue();
                return;
            case JsonValueKind.Object when Opaco(categoria):
            case JsonValueKind.Array when Opaco(categoria):
                escritor.WriteStringValue(Marcador(categoria, null));
                return;
            case JsonValueKind.Object:
                escritor.WriteStartObject();
                var chavesOmitidas = 0;
                foreach (var propriedade in elemento.EnumerateObject())
                {
                    if (!ChaveSegura(propriedade.Name))
                    {
                        escritor.WriteString($"[chave omitida {++chavesOmitidas}]", MarcadorNaoRevisado);
                        continue;
                    }

                    escritor.WritePropertyName(propriedade.Name);
                    var filho = categoria == CategoriaCampoAuditoria.Erros ? CategoriaCampoAuditoria.TextoLivre : Classificar(propriedade.Name);
                    EscreverValor(escritor, propriedade.Value, filho, profundidade + 1);
                }

                escritor.WriteEndObject();
                return;
            case JsonValueKind.Array:
                escritor.WriteStartArray();
                var itens = 0;
                foreach (var item in elemento.EnumerateArray())
                {
                    if (++itens <= ItensMaximos)
                    {
                        EscreverValor(escritor, item, categoria, profundidade + 1);
                    }
                }

                if (itens > ItensMaximos)
                {
                    escritor.WriteStringValue($"[{itens - ItensMaximos} itens omitidos]");
                }

                escritor.WriteEndArray();
                return;
            case JsonValueKind.True or JsonValueKind.False when !Opaco(categoria) && categoria != CategoriaCampoAuditoria.TextoLivre:
                escritor.WriteBooleanValue(elemento.GetBoolean());
                return;
            case JsonValueKind.Number when NumeroPermitido(categoria, elemento):
                escritor.WriteRawValue(elemento.GetRawText(), skipInputValidation: true);
                return;
            case JsonValueKind.String:
                var texto = elemento.GetString()!;
                escritor.WriteStringValue(ManterTexto(categoria, texto) ?? Marcador(categoria, texto));
                return;
            default:
                escritor.WriteStringValue(Marcador(categoria, null));
                return;
        }
    }

    private static bool Opaco(CategoriaCampoAuditoria categoria) =>
        categoria is CategoriaCampoAuditoria.Credencial or CategoriaCampoAuditoria.Pessoal or CategoriaCampoAuditoria.NaoRevisado;

    /// <summary>Números até 9 dígitos na parte inteira: CPF e telefone (10 ou 11 dígitos) não passam nem em campo numérico revisado.</summary>
    private static bool NumeroPermitido(CategoriaCampoAuditoria categoria, JsonElement elemento) => categoria switch
    {
        CategoriaCampoAuditoria.Numero or CategoriaCampoAuditoria.Estrutura or CategoriaCampoAuditoria.Erros =>
            elemento.TryGetDecimal(out var numero) && Math.Abs(numero) < 1_000_000_000m,
        CategoriaCampoAuditoria.Identificador or CategoriaCampoAuditoria.Enumeracao =>
            elemento.TryGetInt64(out var inteiro) && Math.Abs(inteiro) <= 999_999_999,
        _ => false,
    };

    private static void EscreverCabecalhos(Utf8JsonWriter escritor, IHeaderDictionary cabecalhos, (string Nome, Func<string, string?> Valor)[] permitidos)
    {
        foreach (var (nome, valor) in permitidos)
        {
            if (cabecalhos.TryGetValue(nome, out var valores) && valores.Count > 0)
            {
                escritor.WriteString(nome, valor(valores.ToString()) ?? MarcadorFormato);
            }
        }

        foreach (var nome in CabecalhosCredencial.Where(cabecalhos.ContainsKey).Order(StringComparer.OrdinalIgnoreCase))
        {
            escritor.WriteString(nome, MarcadorCredencial);
        }
    }

    private static string? DesafioAutenticacao(string valor)
    {
        var desafio = Desafio().Match(valor);
        if (!desafio.Success)
        {
            return null;
        }

        return desafio.Groups[2].Success ? $"{desafio.Groups[1].Value} error=\"{desafio.Groups[2].Value}\"" : desafio.Groups[1].Value;
    }

    private static bool Identificador(string valor) =>
        Guid.TryParse(valor, out _) || InteiroCurto().IsMatch(valor) || TraceHex().IsMatch(valor) || TraceParent().IsMatch(valor)
        || TraceAspNet().IsMatch(valor);

    /// <summary>
    /// Lista enviada pelo cliente (Accept, Accept-Language...): fora os pesos q=, no máximo 5 dígitos. CPF e telefone, mesmo com
    /// ponto e hífen, nunca passam; os valores reais dos navegadores (pt-BR,pt;q=0.9,es-419;q=0.8) passam inteiros.
    /// </summary>
    private static string? ListaDoCliente(string valor) =>
        Lista().IsMatch(valor) && ContarDigitos(PesoQ().Replace(valor, string.Empty)) < 6 ? valor : null;

    private static bool TextoControlado(string valor) =>
        valor.Length <= 300 && !valor.Contains('@') && ContarDigitos(valor) < 6 && !LetrasDeControle().IsMatch(valor);

    private static bool ChaveSegura(string chave) => Chave().IsMatch(chave) || Guid.TryParse(chave, out _);

    private static int ContarDigitos(string valor) => valor.Count(char.IsAsciiDigit);

    private static string RemoverAcentos(string texto)
    {
        var decomposto = texto.Normalize(NormalizationForm.FormD);
        var saida = new StringBuilder(decomposto.Length);
        foreach (var c in decomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                saida.Append(c);
            }
        }

        return saida.ToString();
    }

    private static Dictionary<string, CategoriaCampoAuditoria> Montar(Dictionary<CategoriaCampoAuditoria, string[]> porCategoria) =>
        porCategoria.SelectMany(par => par.Value.Select(nome => (nome, par.Key))).ToDictionary(par => par.nome, par => par.Key);

    [GeneratedRegex(@"^-?\d{1,9}$")]
    private static partial Regex InteiroCurto();

    [GeneratedRegex(@"^-?\d{1,9}([.,]\d{1,6})?$")]
    private static partial Regex NumeroTexto();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})?)?$")]
    private static partial Regex DataIso();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,60}$")]
    private static partial Regex Codigo();

    [GeneratedRegex(@"^https?://[A-Za-z0-9.\-]+(:\d{1,5})?(/[A-Za-z0-9._~/#%\-]*)?$")]
    private static partial Regex UrlPublica();

    [GeneratedRegex(@"^[0-9a-fA-F]{16,64}$")]
    private static partial Regex TraceHex();

    [GeneratedRegex(@"^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")]
    private static partial Regex TraceParent();

    [GeneratedRegex(@"^[0-9A-Z]{13}:[0-9A-F]{8}$")]
    private static partial Regex TraceAspNet();

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$.\-\[\]]{0,80}$")]
    private static partial Regex Chave();

    [GeneratedRegex(@"^([A-Za-z0-9\-]{1,63}\.)*[A-Za-z0-9\-]{1,63}(:\d{1,5})?$|^\[[0-9A-Fa-f:.]{2,45}\](:\d{1,5})?$")]
    private static partial Regex Host();

    [GeneratedRegex(@"^https?://[A-Za-z0-9.\-]{1,253}(:\d{1,5})?$")]
    private static partial Regex Origem();

    [GeneratedRegex(@"^[a-z0-9!#$&^_.+\-]{1,64}/[a-z0-9!#$&^_.+\-]{1,64}$")]
    private static partial Regex TipoMidia();

    [GeneratedRegex(@"^[A-Za-z0-9*/+.;=,_ \-]{0,200}$")]
    private static partial Regex Lista();

    [GeneratedRegex(@"^\d{1,15}$")]
    private static partial Regex Digitos();

    [GeneratedRegex(@"\d{9,}")]
    private static partial Regex DigitosLongos();

    [GeneratedRegex(@";\s*q=[01](\.\d{0,3})?")]
    private static partial Regex PesoQ();

    /// <summary>CPF (000.000.000-00, com espaços ou sem hífen) e telefone (98765-4321, 3333 4444) com separadores.</summary>
    [GeneratedRegex(@"\d{3}[.\s]\d{3}[.\s]\d{3}[-.\s/]?\d{2}|\d{4,5}[-\s]\d{4}")]
    private static partial Regex DocumentoFormatado();

    [GeneratedRegex(@"\p{C}")]
    private static partial Regex LetrasDeControle();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9\-]{0,30})(?:.*?\berror=""([a-z_]{1,40})"")?")]
    private static partial Regex Desafio();
}
