namespace Almirante.Api.Infrastructure;

public static class HttpContextExtensions
{
    // IP observado pela conexão depois do middleware de proxies confiáveis (ForwardedHeaders); nunca lido de
    // X-Forwarded-For manualmente nem do corpo. IPv4 mapeado em IPv6 vira IPv4 puro.
    public static string IpDeOrigem(this HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return "0.0.0.0";
        }

        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
