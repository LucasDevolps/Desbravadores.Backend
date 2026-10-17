using Almirante.Api.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

public sealed class MediaOptionsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "almirante-testes-media", Guid.NewGuid().ToString("N"));

    public MediaOptionsTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private sealed class Ambiente(string contentRoot, string? webRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Almirante.Api";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = webRoot!;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AmbienteSemWeb(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Almirante.Api";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private string Site => Path.Combine(_base, "site");

    private ValidateOptionsResult Validar(string? pasta, Action<MediaOptions.ImageSettings>? ajustar = null, IHostEnvironment? ambiente = null)
    {
        var options = new MediaOptions { Storage = { Path = pasta } };
        ajustar?.Invoke(options.Images);
        return new MediaOptionsValidator(ambiente ?? new Ambiente(Site, Path.Combine(Site, "wwwroot"))).Validate(null, options);
    }

    [Fact]
    public void PastaAbsolutaForaDoSite_EGravavel_EhAceita_ESemSobrarArquivoDeSonda()
    {
        var pasta = Path.Combine(_base, "fotos");

        Assert.True(Validar(pasta).Succeeded);
        Assert.True(Directory.Exists(pasta));
        Assert.Empty(Directory.GetFileSystemEntries(pasta));
        Assert.True(Validar(pasta, ambiente: new AmbienteSemWeb(Site)).Succeeded);
        Assert.True(Validar(pasta, ambiente: new Ambiente(Site, webRoot: null)).Succeeded);
    }

    [Theory]
    [InlineData(null, "obrigatório")]
    [InlineData("  ", "obrigatório")]
    [InlineData("fotos/relativa", "absoluto")]
    public void PastaAusenteOuRelativa_EhRecusada(string? pasta, string trecho) =>
        Assert.Contains(trecho, Validar(pasta).FailureMessage);

    [Fact]
    public void PastaDentroDoSite_OuDoWwwroot_OuQueContemOSite_EhRecusada()
    {
        Assert.Contains("dentro do site", Validar(Path.Combine(Site, "fotos")).FailureMessage);
        Assert.Contains("dentro do site", Validar(Path.Combine(Site, "wwwroot", "fotos"), ambiente: new Ambiente(Path.Combine(_base, "outro"), Path.Combine(Site, "wwwroot"))).FailureMessage);
        Assert.Contains("dentro do site", Validar(_base).FailureMessage);
        Assert.Contains("dentro do site", Validar(Site + Path.DirectorySeparatorChar).FailureMessage);
    }

    [Fact]
    public void PastaQueNaoPodeSerCriada_EhRecusadaComMotivo()
    {
        var arquivo = Path.Combine(_base, "eh-um-arquivo");
        File.WriteAllText(arquivo, "x");

        Assert.Contains("não pode ser criada", Validar(Path.Combine(arquivo, "fotos")).FailureMessage);
    }

    [Fact]
    public void LimitesForaDaFaixaOuIncoerentes_SaoRecusados()
    {
        var pasta = Path.Combine(_base, "fotos");

        Assert.Contains("MaxUploadSizeMb", Validar(pasta, i => i.MaxUploadSizeMb = 0).FailureMessage);
        Assert.Contains("MinWidth/MinHeight", Validar(pasta, i => i.MinWidth = 2000).FailureMessage);
        Assert.Contains("MinWidth/MinHeight", Validar(pasta, i => i.MinHeight = 2000).FailureMessage);
        Assert.Contains("MinWebpQuality", Validar(pasta, i => i.MinWebpQuality = 90).FailureMessage);
    }

    [Fact]
    public void Limites_ConvertemMbParaBytes()
    {
        var limites = new MediaOptions.ImageSettings { MaxUploadSizeMb = 10, MaxStoredSizeMb = 0.5, MaxMegapixels = 25 };

        Assert.Equal(10L * 1024 * 1024, limites.MaxUploadBytes);
        Assert.Equal(512L * 1024, limites.MaxStoredBytes);
        Assert.Equal(25_000_000L, limites.MaxPixels);
    }

    [Fact]
    public void Development_SemPastaConfigurada_UsaAPastaDescartavelNoTemp()
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Media:Storage:Path"] = "" });
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<MediaOptions>>().Value;

        Assert.Equal(MediaOptions.CaminhoPadraoDesenvolvimento, options.Storage.Path);
    }

    [Fact]
    public void Producao_SemPastaConfigurada_DerrubaASubidaDoHost()
    {
        using var factory = new ConfiguredApiFactory("Production", new Dictionary<string, string?> { ["Media:Storage:Path"] = "" });

        var erro = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Media:Storage:Path é obrigatório", erro.ToString());
    }
}
