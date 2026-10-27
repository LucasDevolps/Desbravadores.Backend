namespace Almirante.Api.Tests;

/// <summary>
/// Teste que precisa criar links simbólicos. No Windows sem privilégio (sem admin nem Modo de Desenvolvedor) a criação falha antes de o
/// teste chegar ao que ele verifica: o teste é pulado com o motivo, em vez de falhar. No CI (windows-latest e Linux) ele roda.
/// </summary>
public sealed class FactComLinkSimbolicoAttribute : FactAttribute
{
    private static readonly Lazy<bool> Permitido = new(PodeCriarLink);

    public FactComLinkSimbolicoAttribute()
    {
        if (!Permitido.Value)
        {
            Skip = "Este ambiente não permite criar links simbólicos (Windows sem admin nem Modo de Desenvolvedor).";
        }
    }

    private static bool PodeCriarLink()
    {
        var pasta = Path.Combine(Path.GetTempPath(), $"almirante-sonda-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);
        try
        {
            File.CreateSymbolicLink(Path.Combine(pasta, "link"), Path.Combine(pasta, "alvo"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            Directory.Delete(pasta, recursive: true);
        }
    }
}
