using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

// Recusa iniciar em Production com uma connection string do SQL Server que desliga a validação do
// certificado (TrustServerCertificate=True) ou a criptografia (Encrypt=False): ambas permitiriam
// interceptação/MITM na conexão com o banco (issue #36). Em Development, certificado autoassinado
// continua permitido (SQL Server local em container, sem PKI própria).
public sealed class SqlServerConnectionSecurityValidator(IHostEnvironment environment) : IValidateOptions<ConnectionStringsOptions>
{
    public ValidateOptionsResult Validate(string? name, ConnectionStringsOptions options)
    {
        var failures = Validate(options.Almirante, environment.IsProduction())
            .Concat(Validate(options.AlmiranteAdmin, environment.IsProduction(), "AlmiranteAdmin"))
            .ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

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
            yield return $"{ConnectionStringsOptions.SectionName}:{name}: TrustServerCertificate=True não é permitido em Production " +
                "(o cliente deixaria de validar a identidade do SQL Server, permitindo MITM). Configure um certificado confiável no " +
                "SQL Server e use TrustServerCertificate=False.";
        }

        // Encrypt é bool nas versões antigas do driver e SqlConnectionEncryptOption a partir da 5.x
        // (Mandatory/Optional/Strict); a conversão implícita para bool trata Optional (== Encrypt=False
        // explícito) como não criptografado e Mandatory/Strict como criptografado.
        bool encrypt = builder.Encrypt;
        if (!encrypt)
        {
            yield return $"{ConnectionStringsOptions.SectionName}:{name}: Encrypt=False não é permitido em Production " +
                "(a conexão com o SQL Server ficaria sem criptografia). Use Encrypt=True.";
        }
    }
}
