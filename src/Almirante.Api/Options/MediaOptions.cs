using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Options;

// Fotos de usuários (issue #116): onde ficam e os limites do processamento. Os nomes de Media:Images seguem a issue.
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public StorageSettings Storage { get; set; } = new();
    public ImageSettings Images { get; set; } = new();

    // Usado só em Development quando Media:Storage:Path não é informado (descartável, fora do repositório).
    public static string CaminhoPadraoDesenvolvimento => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "almirante", "fotos");

    public sealed class StorageSettings
    {
        // Pasta ABSOLUTA, fora do site (o robocopy /MIR do deploy Windows apaga o que estiver dentro dele) e nunca
        // servida pelo IIS/nginx: a API entrega a foto depois de autenticar e autorizar. Obrigatória fora de Development.
        public string? Path { get; set; }

        // Faxina diária de temporários e de fotos sem referência no banco (FotosCleanupService).
        public bool LimpezaHabilitada { get; set; } = true;
    }

    public sealed class ImageSettings
    {
        [Range(1, 25)] public int MaxUploadSizeMb { get; set; } = 10;
        [Range(0.01, 10.0)] public double MaxStoredSizeMb { get; set; } = 1;
        [Range(64, 4096)] public int MaxWidth { get; set; } = 1024;
        [Range(64, 4096)] public int MaxHeight { get; set; } = 1024;
        [Range(1, 4096)] public int MinWidth { get; set; } = 64;
        [Range(1, 4096)] public int MinHeight { get; set; } = 64;
        [Range(1, 50)] public int MaxMegapixels { get; set; } = 25;
        [Range(1, 100)] public int WebpQuality { get; set; } = 82;
        [Range(1, 100)] public int MinWebpQuality { get; set; } = 60;
        [Range(1, 60_000)] public int ProcessingTimeoutMs { get; set; } = 10_000;
        [Range(1, 16)] public int MaxConcurrentProcessing { get; set; } = 2;
        [Range(1, 1000)] public int UploadsPerUserPerWindow { get; set; } = 10;
        [Range(1, 1440)] public int UploadWindowMinutes { get; set; } = 10;

        public long MaxUploadBytes => MaxUploadSizeMb * 1024L * 1024L;
        public long MaxStoredBytes => (long)(MaxStoredSizeMb * 1024 * 1024);
        public long MaxPixels => MaxMegapixels * 1_000_000L;
    }
}

// Falha clara no startup (ValidateOnStart) em vez de no primeiro upload: pasta ausente, relativa, dentro do site (ou
// contendo o site), sem permissão de escrita, ou limites incoerentes. Nenhum valor de segredo passa por aqui.
public sealed class MediaOptionsValidator(IHostEnvironment environment) : IValidateOptions<MediaOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaOptions options)
    {
        var falhas = new List<string>();
        var resultados = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options.Images, new ValidationContext(options.Images), resultados, validateAllProperties: true))
        {
            falhas.AddRange(resultados.Select(r => $"Media:Images: {r.ErrorMessage}"));
        }

        var images = options.Images;
        if (images.MinWidth > images.MaxWidth || images.MinHeight > images.MaxHeight)
        {
            falhas.Add("Media:Images: MinWidth/MinHeight não podem passar de MaxWidth/MaxHeight.");
        }

        if (images.MinWebpQuality > images.WebpQuality)
        {
            falhas.Add("Media:Images: MinWebpQuality não pode passar de WebpQuality.");
        }

        var falhaPasta = ValidarPasta(options.Storage.Path);
        if (falhaPasta is not null)
        {
            falhas.Add(falhaPasta);
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }

    private string? ValidarPasta(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho))
        {
            return "Media:Storage:Path é obrigatório fora de Development (pasta absoluta das fotos, fora do site).";
        }

        if (!Path.IsPathFullyQualified(caminho))
        {
            return $"Media:Storage:Path ('{caminho}') deve ser um caminho absoluto.";
        }

        var pasta = ComSeparador(Path.GetFullPath(caminho));
        var comparacao = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var raizPublicada in new[] { environment.ContentRootPath, (environment as IWebHostEnvironment)?.WebRootPath })
        {
            if (string.IsNullOrWhiteSpace(raizPublicada))
            {
                continue;
            }

            var raiz = ComSeparador(Path.GetFullPath(raizPublicada));
            if (pasta.StartsWith(raiz, comparacao) || raiz.StartsWith(pasta, comparacao))
            {
                return $"Media:Storage:Path ('{caminho}') não pode ficar dentro do site nem contê-lo ('{raizPublicada}'): o deploy apagaria as fotos e o servidor web poderia publicá-las.";
            }
        }

        try
        {
            DiretorioPrivado.Criar(pasta);
            var sonda = Path.Combine(pasta, $".sonda-{Guid.NewGuid():N}");
            using (new FileStream(sonda, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Media:Storage:Path ('{caminho}') não pode ser criada ou não aceita escrita pela identidade da API: {ex.Message}";
        }
    }

    private static string ComSeparador(string caminho) =>
        Path.EndsInDirectorySeparator(caminho) ? caminho : caminho + Path.DirectorySeparatorChar;
}

// Pastas das fotos: no Linux, só o dono (o usuário da API) entra (0700); no Windows valem as ACLs da pasta raiz,
// herdadas (ver README, IIS).
public static class DiretorioPrivado
{
    public static void Criar(string caminho)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(caminho);
        }
        else
        {
            Directory.CreateDirectory(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
