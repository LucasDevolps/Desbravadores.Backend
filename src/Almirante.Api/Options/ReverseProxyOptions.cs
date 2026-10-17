namespace Almirante.Api.Options;

public class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    public string? TrustedNetworkCidr { get; set; }
}
