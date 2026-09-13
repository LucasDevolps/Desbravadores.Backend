using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Almirante.Api.Data;

// Used only by `dotnet ef` design-time tooling (migrations), so it does not depend on
// the full Program.cs / Aspire service discovery pipeline.
public class AlmiranteDbContextFactory : IDesignTimeDbContextFactory<AlmiranteDbContext>
{
    public AlmiranteDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AlmiranteDbContext>();
        optionsBuilder.UseSqlServer("Server=localhost;Database=almirante;User Id=sa;Password=Design_Time_Only;TrustServerCertificate=True");

        return new AlmiranteDbContext(optionsBuilder.Options);
    }
}
