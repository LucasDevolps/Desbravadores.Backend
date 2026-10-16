using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Almirante.Api.Data;

// Used only by `dotnet ef` design-time tooling (migrations), so it does not depend on
// the full Program.cs / Aspire service discovery pipeline.
//
// TrustServerCertificate=True aqui é exclusivo de design-time (SQL Server local com certificado
// autoassinado) e NUNCA representa a configuração de produção — essa vem de ConnectionStrings:almirante
// (ver Program.cs) e é bloqueada pelo SqlServerConnectionSecurityValidator caso caia nesse mesmo
// padrão inseguro em Production. Não há credencial aqui (autenticação do Windows; `migrations add` nem
// conecta): a API nunca usa "sa", nem em design-time. ALMIRANTE_DESIGN_TIME_CONNECTION permite apontar
// `dotnet ef` para outra instância local sem editar este arquivo, mas também não aceita "sa".
public class AlmiranteDbContextFactory : IDesignTimeDbContextFactory<AlmiranteDbContext>
{
    private const string DefaultDesignTimeConnectionString =
        "Server=localhost;Database=almirante;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True";

    public AlmiranteDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ALMIRANTE_DESIGN_TIME_CONNECTION") ?? DefaultDesignTimeConnectionString;
        if (Almirante.Api.Infrastructure.SqlIdentityPolicy.IsSaName(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).UserID))
            throw new InvalidOperationException("ALMIRANTE_DESIGN_TIME_CONNECTION não pode usar o login 'sa'.");
        var optionsBuilder = new DbContextOptionsBuilder<AlmiranteDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new AlmiranteDbContext(optionsBuilder.Options);
    }
}
