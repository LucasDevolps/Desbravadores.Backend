using System.Reflection;
using Almirante.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

// O EF Core só descobre uma migration quando ela tem [DbContext] e [Migration]. Sem o primeiro, a
// migration é ignorada em silêncio: `has-pending-model-changes` não acusa nada, o provider InMemory
// (EnsureCreated) não percebe, e um banco novo sobe com o schema antigo. Este teste roda no CI sem
// banco e impede que isso volte a acontecer.
public class MigrationsTests
{
    private static readonly Type[] MigrationTypes = typeof(AlmiranteDbContext).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(Migration)) && !t.IsAbstract)
        .ToArray();

    [Fact]
    public void TodasAsMigrations_TemAtributosDbContextEMigration()
    {
        Assert.NotEmpty(MigrationTypes);
        var semAtributos = MigrationTypes
            .Where(t => t.GetCustomAttribute<DbContextAttribute>()?.ContextType != typeof(AlmiranteDbContext)
                || t.GetCustomAttribute<MigrationAttribute>() is null)
            .Select(t => t.Name)
            .ToArray();

        Assert.Empty(semAtributos);
    }

    [Fact]
    public void TodasAsMigrations_SaoDescobertasPeloEfCore()
    {
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options;
        using var db = new AlmiranteDbContext(options);

        var descobertas = db.Database.GetMigrations().ToHashSet();
        var declaradas = MigrationTypes.Select(t => t.GetCustomAttribute<MigrationAttribute>()!.Id);

        Assert.All(declaradas, id => Assert.Contains(id, descobertas));
    }
}
