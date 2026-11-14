using System.Net;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using static Almirante.Api.Tests.NotificacoesTestKit;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class NotificacoesHistoricoSqlServerTests
{
    private const string MigrationAnterior = "20261114125930_AddRbacDinamico";
    private const string MigrationEmTeste = "20261114142207_AddNotificacoesHistorico";

    private static AlmiranteDbContext Contexto(string connectionString) =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(connectionString).Options);

    private static async Task<int> EscalarAsync(string cs, string sql)
    {
        await using var conexao = new SqlConnection(cs);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migration_CriaIndiceDoHistoricoSemFiltro_MantemODePendencias_EDownRemoveSoODoHistorico()
    {
        using var factory = new SqlServerApiFactory();
        var cs = factory.ConnectionString;
        const string Historico = """
            SELECT COUNT(*) FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID('dbo.notificacoes') AND i.name = 'IX_notificacoes_historico' AND i.has_filter = 0
              AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 AND c.name = 'LidaEmUtc')
            """;
        const string Pendentes = "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_notificacoes_pendentes' AND has_filter = 1";

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
        }

        Assert.Equal(1, await EscalarAsync(cs, Historico));
        Assert.Equal(1, await EscalarAsync(cs, Pendentes));
        Assert.Equal(3, await EscalarAsync(cs, """
            SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            WHERE i.name = 'IX_notificacoes_historico' AND ic.is_included_column = 0
            """));

        await using (var db = Contexto(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        }

        Assert.Equal(0, await EscalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_notificacoes_historico'"));
        Assert.Equal(1, await EscalarAsync(cs, Pendentes));
    }

    [Fact]
    public async Task Historico_CursorComEmpateDeData_SegueAOrdemDoSqlServerEmCadaFiltro_SemRepetirNemPular()
    {
        using var factory = new SqlServerApiFactory();
        var (membro, membroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        var (outro, outroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        var mesmaHora = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var ids = await TestHelpers.WithDbAsync(factory, async db =>
        {
            var lista = Enumerable.Range(0, 9).Select(i => new Notificacao
            {
                Id = Guid.NewGuid(), DestinatarioId = i == 8 ? outroId : membroId, Tipo = TipoNotificacao.DebitoCadastrado, Titulo = "t", Mensagem = "m",
                CriadaEmUtc = i < 6 ? mesmaHora : mesmaHora.AddMinutes(i), ChaveOrigem = $"teste:{i}",
            }).ToList();
            db.Notificacoes.AddRange(lista);
            await db.SaveChangesAsync();
            return lista.Select(n => n.Id).ToList();
        });
        foreach (var id in ids.Take(8).Where((_, i) => i % 3 == 0))
        {
            Assert.Equal(HttpStatusCode.OK, (await LerAsync(membro, id)).StatusCode);
        }

        foreach (var filtro in new[] { "Todas", "Lidas", "NaoLidas" })
        {
            var lida = filtro switch { "Lidas" => 1, "NaoLidas" => 0, _ => (int?)null };
            var esperada = await TestHelpers.WithDbAsync(factory, db => db.Database.SqlQuery<Guid>($"""
                SELECT Id AS Value FROM dbo.notificacoes
                WHERE DestinatarioId = {membroId} AND ({lida} IS NULL OR CASE WHEN LidaEmUtc IS NULL THEN 0 ELSE 1 END = {lida})
                ORDER BY CriadaEmUtc DESC, Id DESC
                """).ToListAsync());

            var percorrida = await HistoricoCompletoAsync(membro, filtro);

            Assert.Equal(esperada, percorrida.Select(i => i.Id));
            if (filtro == "Lidas") Assert.All(percorrida, i => Assert.NotNull(i.LidaEmUtc));
            if (filtro == "NaoLidas") Assert.All(percorrida, i => Assert.Null(i.LidaEmUtc));
        }

        Assert.Equal(8, (await HistoricoCompletoAsync(membro)).Count);

        Assert.Equal(5, (await HistoricoAsync(membro, "Lidas", pageSize: 1)).NaoLidas);
        Assert.Equal(ids[8], (await HistoricoCompletoAsync(outro)).Single().Id);
    }
}
