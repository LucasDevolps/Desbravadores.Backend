namespace Almirante.Api.Options;

public class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    // CIDR (ex.: "172.30.0.0/24") da única rede confiável para os headers X-Forwarded-For e
    // X-Forwarded-Proto (o proxy nginx do Docker Compose). Vazio/ausente desativa o
    // encaminhamento de headers (comportamento padrão fora do Docker, ex.: IIS).
    public string? TrustedNetworkCidr { get; set; }
}
