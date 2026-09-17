namespace Almirante.Api.Security;

// Marcadores usados nos arquivos versionados (appsettings, .env.example, docs) para indicar
// "defina este valor fora do Git". Um segredo que ainda contém um deles nunca foi configurado de
// verdade, então a aplicação recusa iniciar/semear com ele em vez de aceitar um valor conhecido.
public static class SecretPlaceholders
{
    private static readonly string[] Markers =
        ["DEFINA", "TROQUE", "SUBSTITUA", "CHANGE_ME", "CHANGEME", "PLACEHOLDER", "BASE64_DE", "EXEMPLO"];

    public static bool IsPlaceholder(string? value) =>
        value is not null && Markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
