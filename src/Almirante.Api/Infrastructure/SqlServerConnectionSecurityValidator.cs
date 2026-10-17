using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Infrastructure;

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

    public const string EnforceSettingName = "Security:RequireTrustedSqlServerCertificate";

    public static bool ShouldEnforce(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsProduction() || configuration.GetValue<bool>(EnforceSettingName);

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
            yield break;
        }

        if (builder.TrustServerCertificate)
        {
            yield return $"{ConnectionStringsOptions.SectionName}:{name}: TrustServerCertificate=True não é permitido neste ambiente (Production ou {EnforceSettingName}=true) " +
                "(o cliente deixaria de validar a identidade do SQL Server, permitindo MITM). Configure um certificado confiável no " +
                "SQL Server e use TrustServerCertificate=False.";
        }

        bool encrypt = builder.Encrypt;
        if (!encrypt)
        {
            yield return $"{ConnectionStringsOptions.SectionName}:{name}: Encrypt=False não é permitido neste ambiente (Production ou {EnforceSettingName}=true) " +
                "(a conexão com o SQL Server ficaria sem criptografia). Use Encrypt=True.";
        }
    }
}
