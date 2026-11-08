using System.Net;
using System.Text.RegularExpressions;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

/// <summary>
/// Auditoria de toda chamada HTTP que chega ao pipeline (issue #158, docs/auditoria-http.md). Fica logo depois de UseForwardedHeaders
/// (IP real só de proxy confiável) e antes de tudo o que pode encerrar a requisição: Swagger, exception handler, status code pages,
/// CORS, rate limiting, autenticação, autorização, controllers, health checks e o hub. Cada chamada gera um único registro, montado e
/// sanitizado quando a resposta termina (OnCompleted: status, cabeçalhos e corpo finais, inclusive o Problem Details do exception
/// handler) e entregue ao <see cref="DiarioAuditoriaHttp"/>; o upgrade de WebSocket é registrado no 101, sem esperar a conexão fechar.
/// Login fica de fora. Nenhuma falha da auditoria altera a resposta nem substitui a exceção original.
/// </summary>
public sealed class AuditoriaHttpMiddleware(RequestDelegate next, PoliticaAuditoriaHttp politica, DiarioAuditoriaHttp diario,
    IOptions<AuditoriaHttpOptions> options, TimeProvider tempo, ILogger<AuditoriaHttpMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Value.Habilitada || PoliticaAuditoriaHttp.Excluida(context))
        {
            await next(context);
            return;
        }

        ChamadaAuditada chamada;
        try
        {
            chamada = new ChamadaAuditada(context, politica, options.Value, tempo);
        }
        catch (Exception ex)
        {
            logger.LogError("Auditoria HTTP não conseguiu acompanhar uma chamada ({Tipo}); a requisição segue sem registro.", ex.GetType().FullName);
            await next(context);
            return;
        }

        context.Response.OnStarting(static estado =>
        {
            var (middleware, chamadaAuditada) = ((AuditoriaHttpMiddleware, ChamadaAuditada))estado;
            chamadaAuditada.RegistrarInicioDaResposta();
            return chamadaAuditada.Contexto.Response.StatusCode == StatusCodes.Status101SwitchingProtocols
                ? middleware.EmitirAsync(chamadaAuditada, upgrade: true)
                : Task.CompletedTask;
        }, (this, chamada));
        context.Response.OnCompleted(static estado =>
        {
            var (middleware, chamadaAuditada) = ((AuditoriaHttpMiddleware, ChamadaAuditada))estado;
            return middleware.EmitirAsync(chamadaAuditada, upgrade: false);
        }, (this, chamada));

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            chamada.ExcecaoNaoTratada = ex;
            throw;
        }
        finally
        {
            await chamada.EncerrarProcessamentoAsync();
        }
    }

    private async Task EmitirAsync(ChamadaAuditada chamada, bool upgrade)
    {
        if (!chamada.MarcarEmitida())
        {
            return;
        }

        AuditoriaHttp registro;
        try
        {
            registro = chamada.Montar(upgrade);
        }
        catch (Exception ex)
        {
            logger.LogError("Falha ao sanitizar a auditoria HTTP {Id} ({Tipo}); gravado só o mínimo, sem conteúdo.", chamada.Id, ex.GetType().FullName);
            registro = chamada.MontarMinimo(upgrade);
        }

        try
        {
            await diario.RegistrarAsync(registro);
        }
        catch (Exception ex)
        {
            logger.LogError("Falha ao registrar a auditoria HTTP {Id} ({Tipo}).", registro.Id, ex.GetType().FullName);
        }
    }
}

/// <summary>Estado de uma chamada auditada, do início (dados de entrada já sanitizados) até o registro final.</summary>
internal sealed partial class ChamadaAuditada
{
    private const string MotivoSeguranca = ConteudoSensivelAttribute.MarcadorOmitido;
    private const string MotivoTipo = "tipo de conteúdo sem sanitização garantida";
    private const string MotivoTamanho = "acima do limite de captura";

    private readonly PoliticaAuditoriaHttp _politica;
    private readonly AuditoriaHttpOptions _opcoes;
    private readonly TimeProvider _tempo;
    private readonly long _inicio;
    private readonly bool _sensivel;
    private readonly EstadoCapturaHttp? _estadoRequest;
    private readonly CapturaCorpoHttp? _capturaRequest;
    private readonly LeituraAuditadaStream? _leitura;
    private readonly Stream? _requestOriginal;
    private readonly IHttpResponseBodyFeature _respostaOriginal;
    private readonly EscritaAuditadaStream _resposta;
    private int _emitida;
    private int? _duracaoMs;
    private bool _iniciadaComClienteConectado;

    public ChamadaAuditada(HttpContext context, PoliticaAuditoriaHttp politica, AuditoriaHttpOptions opcoes, TimeProvider tempo)
    {
        Contexto = context;
        _politica = politica;
        _opcoes = opcoes;
        _tempo = tempo;
        Id = Guid.CreateVersion7();
        DataRequestUtc = tempo.GetUtcNow();
        _inicio = tempo.GetTimestamp();
        TraceId = context.TraceIdSeguro();
        Ip = IpObservado(context.Connection.RemoteIpAddress);
        Metodo = Metodos().IsMatch(context.Request.Method) ? HttpMethods.GetCanonicalizedValue(context.Request.Method) : "OUTRO";
        // Endpoint e valores de rota agora: o exception handler os apaga antes de escrever o Problem Details.
        _sensivel = PoliticaAuditoriaHttp.RotaDeCredenciais(context);
        Url = MontarUrl(context);

        (_estadoRequest, _capturaRequest) = DecidirRequest(context.Request);
        if (_capturaRequest is not null)
        {
            _requestOriginal = context.Request.Body;
            _leitura = new LeituraAuditadaStream(_requestOriginal, _capturaRequest);
            context.Request.Body = _leitura;
        }

        _respostaOriginal = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        _resposta = new EscritaAuditadaStream(_respostaOriginal, DecidirResposta);
        context.Features.Set<IHttpResponseBodyFeature>(_resposta);
    }

    public HttpContext Contexto { get; }
    public Guid Id { get; }
    public DateTimeOffset DataRequestUtc { get; }
    public string TraceId { get; }
    public string Ip { get; }
    public string Metodo { get; }
    public string Url { get; }
    public Exception? ExcecaoNaoTratada { get; set; }

    public bool MarcarEmitida() => Interlocked.Exchange(ref _emitida, 1) == 0;

    /// <summary>Cabeçalhos da resposta saindo (OnStarting): só com o cliente ainda conectado o status conta como enviado.</summary>
    public void RegistrarInicioDaResposta() => _iniciadaComClienteConectado = !Contexto.RequestAborted.IsCancellationRequested;

    /// <summary>Fim do pipeline: mede a duração e devolve corpo de requisição e de resposta originais (o resto do servidor não nos vê).</summary>
    public async Task EncerrarProcessamentoAsync()
    {
        _duracaoMs ??= Duracao();
        try
        {
            await _resposta.FinalizarAsync();
        }
        catch (Exception)
        {
            // Cliente desconectado ou corpo já encerrado: o registro sai como Interrompida e a exceção original (se houver) segue
            // intacta; uma falha aqui nunca pode substituí-la.
        }
        finally
        {
            Contexto.Features.Set(_respostaOriginal);
            if (_requestOriginal is not null)
            {
                Contexto.Request.Body = _requestOriginal;
            }
        }
    }

    public AuditoriaHttp Montar(bool upgrade)
    {
        var context = Contexto;
        var abortada = Abortada(upgrade);
        var (estadoRequest, request) = MontarRequest(abortada);
        var (estadoResponse, response) = MontarResponse(upgrade, abortada);
        var excecao = ExcecaoNaoTratada is { } naoTratada
            ? SanitizadorAuditoriaHttp.SanitizarExcecao(naoTratada, tratada: false)
            : context.Features.Get<IExceptionHandlerFeature>()?.Error is { } tratada
                ? SanitizadorAuditoriaHttp.SanitizarExcecao(tratada, tratada: true)
                : null;

        return new AuditoriaHttp
        {
            Id = Id,
            Url = Url,
            MetodoHttp = Metodo,
            Request = request,
            Response = response,
            StatusCode = Status(upgrade, abortada),
            Headers = SanitizadorAuditoriaHttp.SanitizarCabecalhos(context.Request.Headers, upgrade ? null : context.Response.Headers,
                _politica.LiteralConhecido),
            Exception = excecao,
            DataRequestUtc = DataRequestUtc,
            DuracaoMs = DuracaoFinal(),
            IpUsuario = Ip,
            IdentificacaoUsuario = Usuario(context),
            TraceId = TraceId,
            EstadoCapturaRequest = estadoRequest,
            EstadoCapturaResponse = estadoResponse,
        };
    }

    /// <summary>Registro sem conteúdo nenhum, para quando a própria sanitização falhou: a chamada não some e nada vaza.</summary>
    public AuditoriaHttp MontarMinimo(bool upgrade)
    {
        var falha = SanitizadorAuditoriaHttp.MarcadorCorpo("falha na sanitização", null, null);
        return new AuditoriaHttp
        {
            Id = Id,
            Url = Url,
            MetodoHttp = Metodo,
            Request = falha,
            Response = falha,
            StatusCode = Status(upgrade, Abortada(upgrade)),
            Headers = "{}",
            DataRequestUtc = DataRequestUtc,
            DuracaoMs = DuracaoFinal(),
            IpUsuario = Ip,
            IdentificacaoUsuario = Usuario(Contexto),
            TraceId = TraceId,
            EstadoCapturaRequest = EstadoCapturaHttp.FalhaSanitizacao,
            EstadoCapturaResponse = EstadoCapturaHttp.FalhaSanitizacao,
        };
    }

    private (EstadoCapturaHttp, string) MontarRequest(bool abortada)
    {
        var request = Contexto.Request;
        if (_estadoRequest is { } estado)
        {
            return (estado, estado == EstadoCapturaHttp.SemCorpo
                ? SanitizadorAuditoriaHttp.SemCorpo
                : SanitizadorAuditoriaHttp.MarcadorCorpo(Motivo(estado), request.ContentType, request.ContentLength));
        }

        var captura = _capturaRequest!;
        if (captura.Excedeu)
        {
            // Só um corpo sem Content-Length chega aqui: o declarado acima do limite já foi omitido na entrada, e o declarado dentro dele não excede.
            return (EstadoCapturaHttp.OmitidaPorTamanho, SanitizadorAuditoriaHttp.MarcadorCorpo(MotivoTamanho, request.ContentType, captura.Bytes));
        }

        var completa = _leitura!.FimAtingido || (request.ContentLength is { } tamanho && captura.Bytes >= tamanho);
        if (!completa)
        {
            return abortada
                ? (EstadoCapturaHttp.Interrompida, SanitizadorAuditoriaHttp.MarcadorCorpo("requisição interrompida", request.ContentType, captura.Bytes))
                : (EstadoCapturaHttp.NaoLida, SanitizadorAuditoriaHttp.MarcadorCorpo("corpo não lido pela API", request.ContentType, request.ContentLength));
        }

        return captura.Bytes == 0
            ? (EstadoCapturaHttp.SemCorpo, SanitizadorAuditoriaHttp.SemCorpo)
            : SanitizadorAuditoriaHttp.SanitizarJson(captura.Conteudo, _opcoes.LimiteGravacaoCaracteres);
    }

    private (EstadoCapturaHttp, string?) MontarResponse(bool upgrade, bool abortada)
    {
        if (upgrade)
        {
            return (EstadoCapturaHttp.OmitidaPorTipo, SanitizadorAuditoriaHttp.MarcadorCorpo("WebSocket: frames fora do escopo da auditoria HTTP", null, null));
        }

        var response = Contexto.Response;
        var captura = _resposta.Captura;
        if (abortada)
        {
            return (EstadoCapturaHttp.Interrompida, captura is null && !_iniciadaComClienteConectado
                ? null
                : SanitizadorAuditoriaHttp.MarcadorCorpo("resposta interrompida", response.ContentType, captura?.Bytes ?? 0));
        }

        if (captura is null || captura.Bytes == 0)
        {
            return (EstadoCapturaHttp.SemCorpo, null);
        }

        if (captura.Omissao is { } omissao)
        {
            return (omissao, SanitizadorAuditoriaHttp.MarcadorCorpo(Motivo(omissao), response.ContentType, captura.Bytes));
        }

        if (_resposta.UsouSendFile)
        {
            return (EstadoCapturaHttp.OmitidaPorTipo, SanitizadorAuditoriaHttp.MarcadorCorpo("arquivo enviado diretamente", response.ContentType, captura.Bytes));
        }

        if (captura.Excedeu)
        {
            return (EstadoCapturaHttp.OmitidaPorTamanho, SanitizadorAuditoriaHttp.MarcadorCorpo(MotivoTamanho, response.ContentType, captura.Bytes));
        }

        return SanitizadorAuditoriaHttp.SanitizarJson(captura.Conteudo, _opcoes.LimiteGravacaoCaracteres);
    }

    /// <summary>
    /// Chamada concluída normalmente: o status final (o servidor o envia ao encerrar). Interrompida: só se os cabeçalhos saíram antes da
    /// desconexão; senão nulo, nunca o 200 padrão nem o 499 que o ASP.NET Core anota sem enviar.
    /// </summary>
    /// <summary>Cliente desistiu, ou a aplicação lançou depois de começar a resposta (o servidor corta a conexão). Upgrade nunca conta.</summary>
    private bool Abortada(bool upgrade) =>
        !upgrade && (Contexto.RequestAborted.IsCancellationRequested || (ExcecaoNaoTratada is not null && Contexto.Response.HasStarted));

    private int? Status(bool upgrade, bool abortada)
    {
        var response = Contexto.Response;
        if (upgrade || (!abortada && !Contexto.RequestAborted.IsCancellationRequested))
        {
            return response.StatusCode;
        }

        return _iniciadaComClienteConectado ? response.StatusCode : null;
    }

    private (EstadoCapturaHttp?, CapturaCorpoHttp?) DecidirRequest(HttpRequest request)
    {
        if (!TemCorpo(request))
        {
            return (EstadoCapturaHttp.SemCorpo, null);
        }

        if (_sensivel)
        {
            return (EstadoCapturaHttp.OmitidaPorSeguranca, null);
        }

        if (request.Headers.ContentEncoding.Count > 0 || !SanitizadorAuditoriaHttp.EhJson(request.ContentType))
        {
            return (EstadoCapturaHttp.OmitidaPorTipo, null);
        }

        return request.ContentLength > _opcoes.LimiteCapturaBytes
            ? (EstadoCapturaHttp.OmitidaPorTamanho, null)
            : (null, new CapturaCorpoHttp(_opcoes.LimiteCapturaBytes, null));
    }

    private CapturaCorpoHttp DecidirResposta()
    {
        var response = Contexto.Response;
        EstadoCapturaHttp? omissao = _sensivel
            ? EstadoCapturaHttp.OmitidaPorSeguranca
            : response.Headers.ContentEncoding.Count > 0 || !SanitizadorAuditoriaHttp.EhJson(response.ContentType)
                ? EstadoCapturaHttp.OmitidaPorTipo
                : null;
        return new CapturaCorpoHttp(_opcoes.LimiteCapturaBytes, omissao);
    }

    /// <summary>HTTP/1.x só tem corpo com Content-Length ou Transfer-Encoding; HTTP/2 e 3 dizem pelo próprio protocolo.</summary>
    internal static bool TemCorpo(HttpRequest request)
    {
        if (request.ContentLength is { } tamanho)
        {
            return tamanho > 0;
        }

        if (request.Headers.TransferEncoding.Count > 0)
        {
            return true;
        }

        return !HttpProtocol.IsHttp10(request.Protocol) && !HttpProtocol.IsHttp11(request.Protocol)
            && request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;
    }

    private string MontarUrl(HttpContext context)
    {
        try
        {
            var request = context.Request;
            var url = $"{SanitizadorAuditoriaHttp.SanitizarEsquema(request.Scheme)}://{SanitizadorAuditoriaHttp.SanitizarHost(request.Host)}"
                + SanitizadorAuditoriaHttp.SanitizarCaminho(request.PathBase.Value, null, null, _politica.LiteralConhecido)
                + SanitizadorAuditoriaHttp.SanitizarCaminho(request.Path.Value, (context.GetEndpoint() as RouteEndpoint)?.RoutePattern,
                    request.RouteValues, _politica.LiteralConhecido)
                + SanitizadorAuditoriaHttp.SanitizarQuery(request.QueryString);
            return url.Length <= AuditoriaHttp.UrlMaximo ? url : url[..(AuditoriaHttp.UrlMaximo - 3)] + "...";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or UriFormatException)
        {
            return $"{SanitizadorAuditoriaHttp.SanitizarEsquema(context.Request.Scheme)}://{SanitizadorAuditoriaHttp.MarcadorUrl}";
        }
    }

    /// <summary>Medida no fim do pipeline; antes dele (upgrade emitido na hora), a duração até agora.</summary>
    private int DuracaoFinal() => _duracaoMs ?? Duracao();

    private int Duracao() => (int)Math.Min(int.MaxValue, _tempo.GetElapsedTime(_inicio).TotalMilliseconds);

    internal static string Usuario(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && context.User.TentarObterUsuarioId(out var id) ? id.ToString("D") : AuditoriaHttp.Anonimo;

    /// <summary>IP já resolvido por UseForwardedHeaders (nunca lido de X-Forwarded-For aqui); sem escopo de interface IPv6.</summary>
    private static string IpObservado(IPAddress? ip)
    {
        if (ip is null)
        {
            return AuditoriaHttp.IpIndisponivel;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4().ToString();
        }

        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? new IPAddress(ip.GetAddressBytes()).ToString() : ip.ToString();
    }

    private static string Motivo(EstadoCapturaHttp estado) => estado switch
    {
        EstadoCapturaHttp.OmitidaPorSeguranca => MotivoSeguranca,
        EstadoCapturaHttp.OmitidaPorTamanho => MotivoTamanho,
        _ => MotivoTipo,
    };

    [GeneratedRegex("^[A-Za-z-]{1,16}$")]
    private static partial Regex Metodos();
}
