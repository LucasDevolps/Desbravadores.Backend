using Almirante.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

public class SqlServerConnectionSecurityTests
{
    // ---- Núcleo (sem IHostEnvironment/DI) ----

    [Fact]
    public void Validate_ForaDeProduction_NuncaFalha_MesmoComTrustServerCertificate()
    {
        Assert.Empty(SqlServerConnectionSecurityValidator.Validate(
            "Server=localhost;Database=almirante;TrustServerCertificate=True;Encrypt=False", isProduction: false));
    }

    [Fact]
    public void Validate_SemConnectionString_NaoFalha()
    {
        Assert.Empty(SqlServerConnectionSecurityValidator.Validate(null, isProduction: true));
        Assert.Empty(SqlServerConnectionSecurityValidator.Validate("", isProduction: true));
    }

    [Fact]
    public void Validate_Production_AceitaEncryptTrueETrustServerCertificateFalse()
    {
        Assert.Empty(SqlServerConnectionSecurityValidator.Validate(
            "Server=host;Database=almirante;User Id=x;Password=y;Encrypt=True;TrustServerCertificate=False", isProduction: true));
    }

    [Fact]
    public void Validate_Production_AceitaConnectionStringSemEncryptOuTrustServerCertificate_PoisPadraoDoDriverEhSeguro()
    {
        // Sem essas opções, o driver assume Encrypt=Mandatory e TrustServerCertificate=False.
        Assert.Empty(SqlServerConnectionSecurityValidator.Validate(
            "Server=host;Database=almirante;User Id=x;Password=y", isProduction: true));
    }

    [Fact]
    public void Validate_Production_RejeitaTrustServerCertificateTrue_SemExporAConnectionString()
    {
        const string senha = "SegredoSuperSecreto123";
        var falhas = SqlServerConnectionSecurityValidator.Validate(
            $"Server=host;Database=almirante;User Id=sa;Password={senha};Encrypt=True;TrustServerCertificate=True", isProduction: true).ToList();

        Assert.Single(falhas);
        Assert.Contains("TrustServerCertificate", falhas[0]);
        Assert.DoesNotContain(senha, falhas[0]);
    }

    [Fact]
    public void Validate_Production_RejeitaEncryptFalse_SemExporAConnectionString()
    {
        const string senha = "OutroSegredo456";
        var falhas = SqlServerConnectionSecurityValidator.Validate(
            $"Server=host;Database=almirante;User Id=sa;Password={senha};Encrypt=False", isProduction: true).ToList();

        Assert.Single(falhas);
        Assert.Contains("Encrypt", falhas[0]);
        Assert.DoesNotContain(senha, falhas[0]);
    }

    [Fact]
    public void Validate_Production_RejeitaAmbosAoMesmoTempo()
    {
        var falhas = SqlServerConnectionSecurityValidator.Validate(
            "Server=host;Database=almirante;Encrypt=False;TrustServerCertificate=True", isProduction: true).ToList();

        Assert.Equal(2, falhas.Count);
    }

    [Fact]
    public void Validate_ConnectionStringInvalida_NaoLancaEDeixaFalhaParaOutroLugar()
    {
        Assert.Empty(SqlServerConnectionSecurityValidator.Validate("isto não é uma connection string ; ; ;=", isProduction: true));
    }

    // ---- Startup real (WebApplicationFactory) ----

    [Fact]
    public void Startup_Development_AceitaTrustServerCertificateTrue()
    {
        using var factory = new ConfiguredApiFactory(environment: "Development", overrides: new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = "Server=localhost;Database=ignored;Encrypt=True;TrustServerCertificate=True",
        });
        using var client = factory.CreateClient();
    }

    [Fact]
    public void Startup_Production_AceitaEncryptTrueETrustServerCertificateFalse()
    {
        using var factory = new ConfiguredApiFactory(environment: "Production", overrides: new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = "Server=host;Database=ignored;Encrypt=True;TrustServerCertificate=False",
        });
        using var client = factory.CreateClient();
    }

    [Fact]
    public void Startup_Production_RecusaIniciarComTrustServerCertificateTrue()
    {
        using var factory = new ConfiguredApiFactory(environment: "Production", overrides: new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = "Server=host;Database=ignored;Encrypt=True;TrustServerCertificate=True",
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var validation = Assert.IsType<OptionsValidationException>(Unwrap(exception));
        Assert.Contains("TrustServerCertificate", validation.Message);
    }

    [Fact]
    public void Startup_Production_RecusaIniciarComEncryptFalse()
    {
        using var factory = new ConfiguredApiFactory(environment: "Production", overrides: new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = "Server=host;Database=ignored;Encrypt=False",
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var validation = Assert.IsType<OptionsValidationException>(Unwrap(exception));
        Assert.Contains("Encrypt", validation.Message);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException or System.Reflection.TargetInvocationException && exception.InnerException is not null)
            exception = exception.InnerException!;
        return exception;
    }
}
