namespace Almirante.Api.Security;

public static class SecretPlaceholders
{
    private static readonly string[] Markers =
        ["DEFINA", "TROQUE", "SUBSTITUA", "CHANGE_ME", "CHANGEME", "PLACEHOLDER", "BASE64_DE", "EXEMPLO"];

    public static bool IsPlaceholder(string? value) =>
        value is not null && Markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
