using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace Almirante.Api.Tests;

// Testes contra SQL Server real (container Testcontainers), não contra o provider EF Core
// InMemory usado no restante da suíte. Cobrem exatamente o que a issue #14 pede que seja
// validado sobre banco real — trigger de auditoria (TR_Lancamentos_AuditoriaExclusaoLogica),
// SESSION_CONTEXT, compatibilidade com a cláusula OUTPUT do EF Core e o índice único de
// idempotência sob concorrência real — porque o provider InMemory não reproduz de forma
// confiável nenhum desses comportamentos (não tem triggers, não tem SESSION_CONTEXT, e não
// impõe índices únicos entre instâncias de DbContext de forma equivalente ao SQL Server; ver o
// comentário em LancamentosGeraisTests sobre o teste de concorrência que foi movido para cá).
//
// Requer Docker disponível na máquina/runner (Testcontainers sobe um container
// mcr.microsoft.com/mssql/server:2022-latest por execução da classe). Marcados com
// [Trait("Category", "RequiresDocker")] e excluídos do `dotnet test` padrão do CI
// (.github/workflows/backend-ci.yml usa --filter "Category!=RequiresDocker") para não quebrar o
// pipeline em runners sem Docker. Para rodar localmente:
//   dotnet test backend/Almirante.Api.Tests --filter "Category=RequiresDocker"
[Trait("Category", "RequiresDocker")]
public class LancamentosGeraisAuditoriaSqlServerTests : IClassFixture<LancamentosGeraisAuditoriaSqlServerTests.Fixture>
{
    private readonly Fixture _fixture;

    public LancamentosGeraisAuditoriaSqlServerTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    private static object CorpoValido() => new
    {
        tipo = "Mensalidade",
        categoria = "Clube",
        tipoFluxo = "Entrada",
        valor = 25.00m,
        vencimento = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    };

    private static async Task<HttpResponseMessage> PostGeralAsync(HttpClient client, string idempotencyKey, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Lancamentos/Geral")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteGeralAsync(HttpClient client, Guid id, string motivo)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/Geral/{id}")
        {
            Content = JsonContent.Create(new { motivo }),
        };
        return await client.SendAsync(request);
    }

    private async Task<HttpClient> CriarClienteAdminAsync()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = SqlServerLancamentosGeraisFactory.AdminEmail,
            senha = SqlServerLancamentosGeraisFactory.AdminSenha,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token.AccessToken);
        return client;
    }

    [Fact]
    public async Task Delete_ComSucesso_GeraAuditoriaComResponsavelIpMotivoESnapshot()
    {
        var (client, responsavelId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_fixture.Factory, "TES");
        using var httpClient = client;

        var criar = await PostGeralAsync(httpClient, Guid.NewGuid().ToString(), CorpoValido());
        criar.EnsureSuccessStatusCode();
        var listagem = await (await httpClient.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var lancamento = listagem!.Lancamentos[0];

        var antesDaExclusao = DateTime.UtcNow;
        var delete = await DeleteGeralAsync(httpClient, lancamento.Id, "Lançamento cadastrado incorretamente.");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var auditoria = await db.LancamentosDeletados.AsNoTracking()
            .SingleAsync(a => a.LancamentoId == lancamento.Id);

        Assert.Equal(responsavelId, auditoria.UsuarioResponsavelId);
        Assert.False(string.IsNullOrWhiteSpace(auditoria.IpResponsavel));
        Assert.Equal("Lançamento cadastrado incorretamente.", auditoria.Motivo);
        Assert.InRange(auditoria.ExcluidoEmUtc, antesDaExclusao.AddSeconds(-2), DateTime.UtcNow.AddSeconds(2));

        // Snapshot: reflete o lançamento como ele era antes da exclusão.
        Assert.Equal(lancamento.Tipo, auditoria.Tipo);
        Assert.Equal(lancamento.Categoria, auditoria.Categoria);
        Assert.Equal(lancamento.Valor, auditoria.Valor);
        Assert.Equal(lancamento.Status, auditoria.Status);
        Assert.NotNull(auditoria.OperacaoId);
    }

    [Fact]
    public async Task Delete_Repetido_NaoDuplicaAuditoria()
    {
        using var httpClient = await CriarClienteAdminAsync();
        var criar = await PostGeralAsync(httpClient, Guid.NewGuid().ToString(), CorpoValido());
        var listagem = await (await httpClient.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var id = listagem!.Lancamentos[0].Id;

        var primeira = await DeleteGeralAsync(httpClient, id, "Motivo da primeira tentativa.");
        var segunda = await DeleteGeralAsync(httpClient, id, "Motivo da segunda tentativa.");

        Assert.Equal(HttpStatusCode.NoContent, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, segunda.StatusCode);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var total = await db.LancamentosDeletados.AsNoTracking().CountAsync(a => a.LancamentoId == id);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Trigger_UpdateDeMultiplasLinhas_AuditaUmaLinhaPorLancamentoDesativado()
    {
        await TestHelpers.AddUsuarioAsync(_fixture.Factory, "Membro Trigger 1", "DS");
        await TestHelpers.AddUsuarioAsync(_fixture.Factory, "Membro Trigger 2", "DS");
        var (client, responsavelId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_fixture.Factory, "SEC");
        using var httpClient = client;

        var criar = await PostGeralAsync(httpClient, Guid.NewGuid().ToString(), CorpoValido());
        criar.EnsureSuccessStatusCode();
        var corpo = await criar.Content.ReadFromJsonAsync<LancamentoGeralResponse>();
        var operacaoId = corpo!.OperacaoId;
        Assert.True(corpo.LancamentosCriados >= 2);

        const string ip = "203.0.113.5";
        const string motivo = "Exclusão em lote via teste de trigger.";

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'UsuarioResponsavelId', @value = {responsavelId};");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'IpResponsavelExclusao', @value = {ip};");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'MotivoExclusao', @value = {motivo};");

            var linhasAfetadas = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [Lancamentos] SET [Ativo] = 0 WHERE [OperacaoId] = {operacaoId} AND [Ativo] = 1;");

            await transaction.CommitAsync();

            Assert.Equal(corpo.LancamentosCriados, linhasAfetadas);
        }

        using var verifyScope = _fixture.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var auditorias = await verifyDb.LancamentosDeletados.AsNoTracking()
            .Where(a => a.OperacaoId == operacaoId)
            .ToListAsync();

        Assert.Equal(corpo.LancamentosCriados, auditorias.Count);
        Assert.All(auditorias, a =>
        {
            Assert.Equal(responsavelId, a.UsuarioResponsavelId);
            Assert.Equal(ip, a.IpResponsavel);
            Assert.Equal(motivo, a.Motivo);
        });
        // Uma linha de auditoria por lançamento desativado, nunca uma auditoria "agregada".
        Assert.Equal(auditorias.Select(a => a.LancamentoId).Distinct().Count(), auditorias.Count);
    }

    [Fact]
    public async Task Trigger_SemContextoDeAuditoria_LancaErroEDesfazAExclusao()
    {
        using var httpClient = await CriarClienteAdminAsync();
        var criar = await PostGeralAsync(httpClient, Guid.NewGuid().ToString(), CorpoValido());
        var listagem = await (await httpClient.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var id = listagem!.Lancamentos[0].Id;

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            // Nenhum SESSION_CONTEXT alimentado: o trigger deve rejeitar a desativação.
            var erro = await Record.ExceptionAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [Lancamentos] SET [Ativo] = 0 WHERE [Id] = {id} AND [Ativo] = 1;"));

            Assert.NotNull(erro);

            try
            {
                await transaction.RollbackAsync();
            }
            catch
            {
                // XACT_ABORT dentro do trigger já pode ter revertido/abortado a transação;
                // o rollback aqui é só higiene da conexão local ao teste.
            }
        }

        using var verifyScope = _fixture.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var lancamentoAtual = await verifyDb.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id);
        Assert.True(lancamentoAtual.Ativo);

        var auditoria = await verifyDb.LancamentosDeletados.AsNoTracking().Where(a => a.LancamentoId == id).ToListAsync();
        Assert.Empty(auditoria);
    }

    [Fact]
    public async Task Trigger_ComMotivoVazioNoContexto_LancaErroEDesfazAExclusao()
    {
        var (client, responsavelId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(_fixture.Factory, "DIR");
        using var httpClient = client;
        var criar = await PostGeralAsync(httpClient, Guid.NewGuid().ToString(), CorpoValido());
        var listagem = await (await httpClient.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var id = listagem!.Lancamentos[0].Id;

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'UsuarioResponsavelId', @value = {responsavelId};");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'IpResponsavelExclusao', @value = {"198.51.100.7"};");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'MotivoExclusao', @value = {"   "};");

            var erro = await Record.ExceptionAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [Lancamentos] SET [Ativo] = 0 WHERE [Id] = {id} AND [Ativo] = 1;"));

            Assert.NotNull(erro);

            try
            {
                await transaction.RollbackAsync();
            }
            catch
            {
                // ver comentário equivalente no teste acima.
            }
        }

        using var verifyScope = _fixture.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var lancamentoAtual = await verifyDb.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id);
        Assert.True(lancamentoAtual.Ativo);
    }

    [Fact]
    public async Task UpdateGenericoSemTocarAtivo_NaoGeraAuditoriaEFuncionaComOTriggerPresente()
    {
        // Regressão do requisito 8 (compatibilidade EF Core / cláusula OUTPUT): antes de
        // UseSqlOutputClause(false) em AlmiranteDbContext, este PUT (LancamentosController,
        // gerado via EF Core SaveChanges) falharia contra SQL Server real com o erro "Msg 334"
        // só por existir um trigger AFTER UPDATE em Lancamentos — mesmo esta atualização nunca
        // tocando a coluna Ativo.
        using var httpClient = await CriarClienteAdminAsync();
        var criar = await httpClient.PostAsJsonAsync("/api/Lancamentos", new
        {
            membroNome = "Membro CRUD Genérico",
            tipo = "Outros",
            categoria = "Clube",
            tipoFluxo = "Despesa",
            valor = 10m,
            vencimento = "2026-12-01",
            status = "Pendente",
        });
        criar.EnsureSuccessStatusCode();
        var criado = await criar.Content.ReadFromJsonAsync<LancamentoDto>();

        var atualizar = await httpClient.PutAsJsonAsync($"/api/Lancamentos/{criado!.Id}", new { status = "Pago" });
        Assert.Equal(HttpStatusCode.OK, atualizar.StatusCode);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var auditoria = await db.LancamentosDeletados.AsNoTracking()
            .Where(a => a.LancamentoId == criado.Id)
            .ToListAsync();
        Assert.Empty(auditoria);
    }

    [Fact]
    public async Task SemVazamentoDeContextoEntreRequisicoes_ExclusoesSequenciaisRefletemCadaRequisicao()
    {
        // Max Pool Size=1 força a mesma conexão física a ser reutilizada entre as duas exclusões
        // sequenciais abaixo — exatamente o cenário que SESSION_CONTEXT + limpeza em `finally`
        // (LancamentosGeraisService.DeleteAsync) precisa proteger.
        var connectionStringPoolLimitadoBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 1,
        };
        using var factoryDedicada = SqlServerLancamentosGeraisFactory.CreateAndWarmUp(connectionStringPoolLimitadoBuilder.ConnectionString);

        var (clienteA, responsavelA) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factoryDedicada, "TES");
        using var httpClienteA = clienteA;
        var (clienteB, responsavelB) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factoryDedicada, "SEC");
        using var httpClienteB = clienteB;

        var criarA = await PostGeralAsync(httpClienteA, Guid.NewGuid().ToString(), CorpoValido());
        criarA.EnsureSuccessStatusCode();
        var listaA = await (await httpClienteA.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var idA = listaA!.Lancamentos[0].Id;

        var deleteA = await DeleteGeralAsync(httpClienteA, idA, "Motivo da requisição A.");
        Assert.Equal(HttpStatusCode.NoContent, deleteA.StatusCode);

        var criarB = await PostGeralAsync(httpClienteB, Guid.NewGuid().ToString(), CorpoValido());
        criarB.EnsureSuccessStatusCode();
        var listaB = await (await httpClienteB.GetAsync("/api/Lancamentos/Geral"))
            .Content.ReadFromJsonAsync<LancamentosGeralListResponse>();
        var idB = listaB!.Lancamentos.First(l => l.Id != idA).Id;

        var deleteB = await DeleteGeralAsync(httpClienteB, idB, "Motivo da requisição B.");
        Assert.Equal(HttpStatusCode.NoContent, deleteB.StatusCode);

        using var scope = factoryDedicada.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var auditoriaA = await db.LancamentosDeletados.AsNoTracking().SingleAsync(a => a.LancamentoId == idA);
        var auditoriaB = await db.LancamentosDeletados.AsNoTracking().SingleAsync(a => a.LancamentoId == idB);

        Assert.Equal(responsavelA, auditoriaA.UsuarioResponsavelId);
        Assert.Equal("Motivo da requisição A.", auditoriaA.Motivo);
        Assert.Equal(responsavelB, auditoriaB.UsuarioResponsavelId);
        Assert.Equal("Motivo da requisição B.", auditoriaB.Motivo);
    }

    [Fact]
    public async Task Create_RequisicoesConcorrentesComMesmaChave_CriaApenasUmaOperacao()
    {
        using var httpClient = await CriarClienteAdminAsync();
        var key = Guid.NewGuid().ToString();

        var tarefa1 = PostGeralAsync(httpClient, key, CorpoValido());
        var tarefa2 = PostGeralAsync(httpClient, key, CorpoValido());
        var respostas = await Task.WhenAll(tarefa1, tarefa2);

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var corpos = await Task.WhenAll(respostas.Select(r => r.Content.ReadFromJsonAsync<LancamentoGeralResponse>()));
        Assert.Equal(corpos[0]!.OperacaoId, corpos[1]!.OperacaoId);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var totalOperacoes = await db.LancamentosOperacoes.AsNoTracking().CountAsync(o => o.IdempotencyKey == key);
        Assert.Equal(1, totalOperacoes);

        var totalLancamentos = await db.Lancamentos.AsNoTracking().CountAsync(l => l.OperacaoId == corpos[0]!.OperacaoId);
        Assert.Equal(corpos[0]!.LancamentosCriados, totalLancamentos);
    }

    public class Fixture : IAsyncLifetime
    {
        private MsSqlContainer? _container;

        public SqlServerLancamentosGeraisFactory Factory { get; private set; } = null!;
        public string ConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            // Mesma imagem usada em compose.yaml, para refletir o SQL Server real do deploy.
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();

            var connectionStringBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
            {
                InitialCatalog = "almirante_teste",
            };
            ConnectionString = connectionStringBuilder.ConnectionString;

            // CreateAndWarmUp já força a construção do host (roda DbSeeder.SeedAsync ->
            // migrations reais, incluindo a criação do trigger, contra o container) em vez de na
            // primeira requisição do primeiro teste.
            Factory = SqlServerLancamentosGeraisFactory.CreateAndWarmUp(ConnectionString);

            using var client = Factory.CreateClient();
            var response = await client.GetAsync("/health");
            response.EnsureSuccessStatusCode();
        }

        public async Task DisposeAsync()
        {
            Factory.Dispose();
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
    }
}
