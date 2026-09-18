using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Almirante.Api.Data;

// Used only by `dotnet ef` design-time tooling (migrations), so it does not depend on
// the full Program.cs / Aspire service discovery pipeline.
//
// TrustServerCertificate=True aqui é exclusivo de design-time (SQL Server local com certificado
// autoassinado) e NUNCA representa a configuração de produção — essa vem de ConnectionStrings:almirante
// (ver Program.cs) e é bloqueada pelo SqlServerConnectionSecurityValidator caso caia nesse mesmo
// padrão inseguro em Production. A senha é só um placeholder de design-time, não uma credencial real;
// ALMIRANTE_DESIGN_TIME_CONNECTION permite apontar `dotnet ef` para outra instância local sem editar
// este arquivo.
public class AlmiranteDbContextFactory : IDesignTimeDbContextFactory<AlmiranteDbContext>
{
    private const string DefaultDesignTimeConnectionString =
        "Server=localhost;Database=almirante;User Id=sa;Password=Design_Time_Only;Encrypt=True;TrustServerCertificate=True";

    public AlmiranteDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ALMIRANTE_DESIGN_TIME_CONNECTION") ?? DefaultDesignTimeConnectionString;
        var optionsBuilder = new DbContextOptionsBuilder<AlmiranteDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new AlmiranteDbContext(optionsBuilder.Options);
    }
}
