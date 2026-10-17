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
}
