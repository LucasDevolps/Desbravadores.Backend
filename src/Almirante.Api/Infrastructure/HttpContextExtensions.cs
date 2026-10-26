namespace Almirante.Api.Infrastructure;

public static class HttpContextExtensions
{
    public static string IpDeOrigem(this HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "0.0.0.0";
        }

        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    /// <summary>Correlação segura para auditoria: trace id W3C da requisição ou, sem ele, o identificador do ASP.NET Core (ASCII, até 64).</summary>
    public static string TraceIdSeguro(this HttpContext httpContext)
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString();
        if (string.IsNullOrEmpty(traceId) || traceId.All(c => c == '0'))
        {
            traceId = httpContext.TraceIdentifier;
        }

        var limpo = new string(traceId.Where(c => c is >= '!' and <= '~').Take(64).ToArray());
        return limpo.Length == 0 ? "sem-trace" : limpo;
    }
}
