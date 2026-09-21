using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

// Prova que a migration AddAtualizadoPorUsuarioIdToLancamentos (issue #50) é não destrutiva sobre um
// banco já populado. Nenhum teste existente cobre isso: LancamentosTests roda em InMemory (não aplica
// migrations reais) e simula o valor nulo escrevendo direto na entidade; SqlServerIntegrationTests só
// aplica todas as migrations sobre um banco novo, sempre vazio.
//
// Estratégia: aplica o schema só até a migration ANTERIOR a esta, insere um usuário e um lançamento
// "legados" (sem a coluna nova), aplica a migration em teste por cima desses dados, e só então sobe a
// aplicação normalmente para exercitar um PUT autenticado nesse mesmo lançamento histórico.
[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentosAuditMigrationTests
{
    // Migration imediatamente anterior a AddAtualizadoPorUsuarioIdToLancamentos.
    private const string MigrationAnterior = "20260917130859_AddLoginLockout";

    [Fact]
    public async Task Migration_PreservaLancamentoHistorico_EAplicaIndiceEFkRestritivos()
    {
        using var factory = new SqlServerApiFactory();
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(factory.ConnectionString).Options;

        Guid lancamentoId;
        var vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToDateTime(TimeOnly.MinValue);
        var dataCriacaoLegado = DateTime.UtcNow.AddDays(-400);

        await using (var db = new AlmiranteDbContext(options))
        {
            // Schema só até a migration anterior: a tabela Lancamentos ainda NÃO tem
            // AtualizadoPorUsuarioId neste ponto, embora o modelo compilado do DbContext (mesma
            // classe usada em produção) já a inclua — por isso o insert do lançamento abaixo usa SQL
            // bruto (só ele precisa disso; Cargos/Usuarios não mudaram desde esta migration).
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(MigrationAnterior);

            var cargoDs = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Membro do clube.", CriadoPor = "Teste", Role = "DS" };
            var cargoDir = new Cargo { Id = Guid.NewGuid(), Nome = "Diretor", Descricao = "Diretoria do clube.", CriadoPor = "Teste", Role = "DIR" };
            var cargoAdm = new Cargo { Id = Guid.NewGuid(), Nome = "Administrador", Descricao = "Acesso administrativo.", CriadoPor = "Teste", Role = "ADM" };
            db.Cargos.AddRange(cargoDs, cargoDir, cargoAdm);

            var membroLegado = new Usuario
            {
                Id = Guid.NewGuid(),
                Nome = "Legado",
                Email = "legado@local.dev",
                EmailNormalizado = "LEGADO@LOCAL.DEV",
                SenhaHash = "hash-nao-usado-neste-teste",
                CargoId = cargoDs.Id,
                DataCriacao = dataCriacaoLegado,
            };
            db.Usuarios.Add(membroLegado);
            await db.SaveChangesAsync();

            lancamentoId = Guid.NewGuid();
            var membroId = membroLegado.Id;
            var descricao = "Lançamento anterior à coluna de auditoria";

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO Lancamentos
                    (Id, MembroId, Finalidade, Descricao, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, DataAtualizacao, Ativo, OperacaoId)
                VALUES
                    ({lancamentoId}, {membroId}, {"Mensalidade"}, {descricao}, {"Clube"}, {20m},
                     {vencimento}, {"Pendente"}, {"Entrada"}, {dataCriacaoLegado}, {(DateTime?)null}, {true}, {(Guid?)null})");

            // Aplica a migration em teste por cima dos dados legados acima.
            await migrator.MigrateAsync();
        }

        await using (var db = new AlmiranteDbContext(options))
        {
            var lancamento = await db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == lancamentoId);
            Assert.Null(lancamento.AtualizadoPorUsuarioId);
            Assert.Equal("Lançamento anterior à coluna de auditoria", lancamento.Descricao);

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            try
            {
                await using var fkCommand = connection.CreateCommand();
                fkCommand.CommandText = "SELECT delete_referential_action_desc FROM sys.foreign_keys WHERE name = 'FK_Lancamentos_Usuarios_AtualizadoPorUsuarioId'";
                Assert.Equal("NO_ACTION", (string)(await fkCommand.ExecuteScalarAsync())!);

                await using var indexCommand = connection.CreateCommand();
                indexCommand.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Lancamentos_AtualizadoPorUsuarioId'";
                Assert.Equal(1, (int)(await indexCommand.ExecuteScalarAsync())!);
            }
            finally
            {
                await connection.CloseAsync();
            }
        }

        // Sobe a aplicação normalmente sobre o MESMO banco (já migrado até o fim — o MigrateAsync
        // automático do DbSeeder é um no-op; Cargos/Lancamentos não vazios fazem o seed padrão pular
        // a inserção dos cargos/lançamento de exemplo, sem conflitar com os dados acima).
        var (client, atorId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DIR");

        // Mesma proteção já coberta em LancamentosTests.Update_IgnoraUsuarioResponsavelEnviadoNoBody:
        // "usuarioResponsavelId" no body é ignorado; o responsável vem só da identidade autenticada.
        var forjado = Guid.NewGuid();
        var response = await client.PutAsJsonAsync($"/api/Lancamentos/{lancamentoId}", new { status = "Pago", usuarioResponsavelId = forjado });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var atualizado = await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == lancamentoId));
        Assert.Equal(atorId, atualizado.AtualizadoPorUsuarioId);
        Assert.NotEqual(forjado, atualizado.AtualizadoPorUsuarioId);
        Assert.NotNull(atualizado.DataAtualizacao);
    }
}
