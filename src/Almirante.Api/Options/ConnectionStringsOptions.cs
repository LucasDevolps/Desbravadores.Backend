namespace Almirante.Api.Options;

public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";

    public string? Almirante { get; init; }

    public string? AlmiranteAdmin { get; init; }
}
