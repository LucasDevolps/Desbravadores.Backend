using System.Reflection;
using Almirante.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

// Não acessa banco: roda no CI comum. Os testes InMemory não aplicam migrations, então sem esta
// verificação uma migration ignorada pelo EF (ex.: sem [DbContext]) só apareceria em produção.
public class MigrationsTests
{
    private static AlmiranteDbContext SqlServerContextSemConexao() =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer("Server=nao-conecta;Database=x").Options);

    [Fact]
    public void TodasAsMigrationsDoAssemblySaoDescobertasPeloEf()
    {
        var declaradas = typeof(AlmiranteDbContext).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Migration)) && !t.IsAbstract)
            .ToList();

        Assert.All(declaradas, t => Assert.Equal(typeof(AlmiranteDbContext), t.GetCustomAttribute<DbContextAttribute>()?.ContextType));

        using var db = SqlServerContextSemConexao();
        var descobertas = db.Database.GetMigrations().ToList();
        Assert.Equal(declaradas.Select(t => t.GetCustomAttribute<MigrationAttribute>()!.Id).Order(), descobertas.Order());
        Assert.Contains("20260917120000_UnifyLancamentosFlow", descobertas);
    }

    [Fact]
    public void SnapshotCorrespondeAoModelo()
    {
        using var db = SqlServerContextSemConexao();
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
