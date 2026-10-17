namespace Almirante.Api.Options;

public sealed class DbCredentialOptions
{
    public const string SectionName = "DbCredentials";

    public string? AppUser { get; init; }

    public int RotationHours { get; init; } = 24;

    public bool Enabled => !string.IsNullOrWhiteSpace(AppUser);
}
