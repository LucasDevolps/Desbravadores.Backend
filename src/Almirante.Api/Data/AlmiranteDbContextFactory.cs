using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Almirante.Api.Data;

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
