using Almirante.Api.Data;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class DbPrivilegeTests
{
    private static async Task<SqlIdentityEnvironment> PreparedAsync()
    {
        var env = new SqlIdentityEnvironment();
        try
        {
            await env.BootstrapAsync();
            await env.NewManager().InitializeAsync();
            return env;
        }
        catch
        {
            await env.DisposeAsync();
            throw;
        }
    }

    private static async Task<Guid> GuidAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SeedAsync(SqlIdentityEnvironment env)
    {
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(env.AppCs).AddInterceptors(new AppDbCredentialInterceptor(env.Provider)).Options;
        await using var db = new AlmiranteDbContext(options);
        await DbSeeder.SeedAsync(db, new PasswordHasher<Almirante.Api.Entities.Usuario>(),
            Microsoft.Extensions.Options.Options.Create(new SeedOptions { Nome = "Admin", Email = "admin@local.dev", Senha = "Tst-Senha-Longa-2026-ok" }));
    }

    [Fact]
    public async Task ImpersonateEmDbo_EDetectadoPelaAuditoria_ERevogadoPeloBootstrap()
    {
        await using var env = await PreparedAsync();

        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"GRANT IMPERSONATE ON USER::[dbo] TO [{env.AppUser}];");
        }

        await using (var comprometido = await env.OpenAppAsync())
        {
            Assert.Equal(1, await ScalarAsync(comprometido, "SELECT HAS_PERMS_BY_NAME('dbo','USER','IMPERSONATE')"));
            Assert.Contains(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(comprometido),
                f => f.Contains("IMPERSONATE", StringComparison.OrdinalIgnoreCase));
        }

        await env.BootstrapAsync(env.AdminPassword);

        await using var app = await env.OpenAppAsync();
        Assert.Equal(0, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME('dbo','USER','IMPERSONATE')"));
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));
    }

    [Fact]
    public async Task ConcessaoDiretaExcessivaAoRuntime_EDetectadaPelaAuditoria_ERevogadaPeloBootstrap()
    {
        await using var env = await PreparedAsync();
        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"GRANT DELETE ON OBJECT::dbo.Lancamentos TO [{env.AppUser}]; GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.lancamentos_deletados TO [{env.AppUser}]; GRANT ALTER ON OBJECT::dbo.Cargos TO [{env.AppUser}]");
        }

        await using (var comprometido = await env.OpenAppAsync())
        {
            var achados = await DbPrivilegeAuditor.FindExcessPrivilegesAsync(comprometido);
            Assert.Contains("DELETE em Lancamentos", achados);
            Assert.DoesNotContain(achados, f => f.StartsWith("INSERT direto na tabela de auditoria", StringComparison.Ordinal));
            Assert.Contains("ALTER em Cargos", achados);
        }

        await env.BootstrapAsync(env.AdminPassword);
        await env.NewManager().InitializeAsync();
        await using var app = await env.OpenAppAsync();
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));
    }

    [Fact]
    public async Task Auditoria_AplicacaoNaoFalsificaNemAlteraHistorico_MasTriggerContinuaGravando()
    {
        await using var env = await PreparedAsync();
        await SeedAsync(env);

        Guid usuarioId, lancamentoId;
        await using (var admin = await env.OpenAdminAsync())
        {
            usuarioId = await GuidAsync(admin, "SELECT TOP 1 Id FROM Usuarios");
            lancamentoId = await GuidAsync(admin, "SELECT TOP 1 Id FROM Lancamentos WHERE Ativo = 1");
        }

        await using var app = await env.OpenAppAsync();

        foreach (var sql in new[]
        {
            "INSERT INTO lancamentos_deletados (Id, LancamentoId, UsuarioResponsavelId, IpResponsavel, Motivo, MembroId, Finalidade, Categoria, TipoFluxo, Valor, Vencimento, Status, DataCriacaoOriginal) " +
            $"VALUES (NEWID(), '{lancamentoId}', '{usuarioId}', '203.0.113.9', N'fabricado', NULL, N'x', N'Clube', N'Entrada', 999, SYSDATETIME(), N'Pendente', SYSUTCDATETIME())",
            "UPDATE lancamentos_deletados SET Motivo = N'alterado'",
            "DELETE FROM lancamentos_deletados",
        })
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(app, sql));
            Assert.Equal(229, ex.Number);
        }

        await ExecAsync(app, $"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value='{usuarioId}';
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='198.51.100.23';
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=N'duplicado';
            UPDATE dbo.Lancamentos SET Ativo = 0 WHERE Id = '{lancamentoId}';
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=NULL;
            """);

        await using var verificacao = await env.OpenAdminAsync();
        Assert.Equal(1, await ScalarAsync(verificacao, "SELECT COUNT(*) FROM lancamentos_deletados"));
        Assert.Equal(1, await ScalarAsync(verificacao,
            $"SELECT COUNT(*) FROM lancamentos_deletados WHERE LancamentoId = '{lancamentoId}' AND UsuarioResponsavelId = '{usuarioId}' AND IpResponsavel = '198.51.100.23' AND Motivo = N'duplicado'"));
    }
}
