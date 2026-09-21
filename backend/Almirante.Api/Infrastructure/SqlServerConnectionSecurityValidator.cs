using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

// Recusa iniciar em Production (ou com Security:RequireTrustedSqlServerCertificate=true) com uma connection string do SQL Server que desliga a validação do
// certificado (TrustServerCertificate=True) ou a criptografia (Encrypt=False): ambas permitiriam
// interceptação/MITM na conexão com o banco (issue #36). Em Development, certificado autoassinado
// continua permitido (SQL Server local em container, sem PKI própria).
public sealed class SqlServerConnectionSecurityValidator(IHostEnvironment environment, IConfiguration configuration) : IValidateOptions<ConnectionStringsOptions>
{
    public ValidateOptionsResult Validate(string? name, ConnectionStringsOptions options)
    {
        var enforce = ShouldEnforce(environment, configuration);
        var failures = Validate(options.Almirante, enforce)
            .Concat(Validate(options.AlmiranteAdmin, enforce, "AlmiranteAdmin"))
            .ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // Em Production a política é sempre obrigatória. Um Development *publicado* (IIS, Pop!_OS) usa o mesmo
    // banco/rede de um ambiente real e também precisa dela: ele a ativa com
    // Security:RequireTrustedSqlServerCertificate=true (env Security__RequireTrustedSqlServerCertificate)
    // depois de instalar o certificado do SQL Server nos clientes. Development local segue tolerante.
    public const string EnforceSettingName = "Security:RequireTrustedSqlServerCertificate";

    public static bool ShouldEnforce(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsProduction() || configuration.GetValue<bool>(EnforceSettingName);

    // Núcleo testável isoladamente, sem IHostEnvironment. Nunca inclui a connection string (nem
    // trechos dela) nas mensagens de erro — só os nomes das opções problemáticas — para que a falha
    // de startup não vaze usuário/senha em log ou exceção.
    public static IEnumerable<string> Validate(string? connectionString, bool isProduction, string name = "Almirante")
    {
        if (!isProduction || string.IsNullOrWhiteSpace(connectionString)) yield break;

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            // Connection string malformada: falha ao abrir a conexão logo em seguida, com mensagem
            // própria do driver; não é papel desta validação de política de TLS.
            yield break;
        }

        if (builder.TrustServerCertificate)
        {
            yield return $"{ConnectionStringsOptions.SectionName}:{name}: TrustServerCertificate=True não é permitido neste ambiente (Production ou {EnforceSettingName}=true) " +
                "(o cliente deixaria de validar a identidade do SQL Server, permitindo MITM). Configure um certificado confiável no " +
                "SQL Server e use TrustServerCertificate=False.";
        }

        // Encrypt é bool nas versões antigas do driver e SqlConnectionEncryptOption a partir da 5.x
        // (Mandatory/Optional/Strict); a conversão implícita para bool trata Optional (== Encrypt=False
        // explícito) como não criptografado e Mandatory/Strict como criptografado.
        bool encrypt = builder.Encrypt;
        if (!encrypt)
        {
            yield return $"{ConnectionStringsOptions.SectionName}:{name}: Encrypt=False não é permitido neste ambiente (Production ou {EnforceSettingName}=true) " +
                "(a conexão com o SQL Server ficaria sem criptografia). Use Encrypt=True.";
        }
    }
}
