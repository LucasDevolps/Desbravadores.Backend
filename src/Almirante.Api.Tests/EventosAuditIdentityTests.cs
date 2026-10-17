using Almirante.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class EventosAuditIdentityTests
{
    private static async Task<SqlException> NegadoAsync(SqlConnection c, string sql) =>
        await Assert.ThrowsAnyAsync<SqlException>(() => ExecAsync(c, sql));

    private static async Task<(Guid Evento, Guid Membro)> SemearEventoAsync(SqlIdentityEnvironment env)
    {
        await using var h = await OpenHarnessAsync(env.Database);
        var (cargo, membro, evento, lancamento) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecAsync(h, $"""
            INSERT INTO dbo.Cargos (Id, Nome, Descricao, CriadoPor, CriadoEm, Role, Ativo) VALUES ('{cargo}', N'Cargo', N'x', N'teste', SYSUTCDATETIME(), N'DS', 1);
            INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas)
                VALUES ('{membro}', N'Membro', N'm@x.dev', N'M@X.DEV', N'x', '{cargo}', SYSUTCDATETIME(), 0, 0);
            INSERT INTO dbo.eventos (Id, EventoGrupoId, DataEvento, [Local], TransporteEhGratis, TransporteValor, AlimentacaoIndividual, AlimentacaoValor,
                                     SeguroObrigatorio, ValorPorMembro, Ativo, CriadoPorUsuarioId, CriadoEmUtc)
                VALUES ('{evento}', '{evento}', '2026-10-18', N'Parque', 0, 10, 0, 8, 2, 20, 1, '{membro}', SYSUTCDATETIME());
            INSERT INTO dbo.Lancamentos (Id, MembroId, Finalidade, Descricao, Categoria, Valor, Vencimento, Status, TipoFluxo, DataCriacao, Ativo, EventoId)
                VALUES ('{lancamento}', '{membro}', NULL, N'evento', 0, 20, '2026-10-18', 0, 0, SYSUTCDATETIME(), 1, '{evento}');
            INSERT INTO dbo.evento_membros (Id, EventoId, EventoGrupoId, MembroId, LancamentoId, Ativo, CriadoEmUtc)
                VALUES (NEWID(), '{evento}', '{evento}', '{membro}', '{lancamento}', 1, SYSUTCDATETIME());
            """);
        return (evento, membro);
    }

    [Fact]
    public async Task IdentidadeDeRuntime_NaoEscreveNemApagaHistoricoEventos_TriggerFalhaSemContexto_ETriggerGravaComContexto()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        await env.NewManager().InitializeAsync();
        var (evento, membro) = await SemearEventoAsync(env);
        await using var app = await env.OpenAppAsync();

        Assert.Equal(env.AppUser, (await ColumnAsync(app, "SELECT USER_NAME()")).Single());
        Assert.NotEqual("sa", (await ColumnAsync(app, "SELECT LOWER(SUSER_SNAME())")).Single());
        Assert.NotEqual(env.AdminUser, env.AppUser);
        foreach (var papel in new[] { "db_owner", "db_datawriter", "db_ddladmin", "db_securityadmin", "db_accessadmin", "db_backupoperator" })
            Assert.Equal(0, await ScalarAsync(app, $"SELECT ISNULL(IS_MEMBER(N'{papel}'), 0)"));
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));

        Assert.Equal(229, (await NegadoAsync(app, $"INSERT INTO dbo.historico_eventos (Id, EventoId, EventoGrupoId, DataEvento, [Local], TransporteEhGratis, TransporteValor, AlimentacaoIndividual, AlimentacaoValor, SeguroObrigatorio, ValorPorMembro, QuantidadeMembros, Total, ParticipantesJson, UsuarioResponsavelId, IpResponsavel, Motivo, ExcluidoEmUtc) VALUES (NEWID(), '{evento}', '{evento}', '2026-10-18', N'x', 0, 0, 0, 0, 0, 0, 0, 0, N'[]', NEWID(), '1.1.1.1', N'forjado', SYSUTCDATETIME())")).Number);
        Assert.Equal(229, (await NegadoAsync(app, "UPDATE dbo.historico_eventos SET Motivo = N'adulterado'")).Number);
        Assert.Equal(229, (await NegadoAsync(app, "DELETE FROM dbo.historico_eventos")).Number);
        Assert.True((await NegadoAsync(app, "TRUNCATE TABLE dbo.historico_eventos")).Number is 229 or 4701 or 1088);
        Assert.Equal(229, (await NegadoAsync(app, "DELETE FROM dbo.eventos")).Number);
        Assert.Equal(229, (await NegadoAsync(app, "DELETE FROM dbo.evento_membros")).Number);
        Assert.Equal(229, (await NegadoAsync(app, "DELETE FROM dbo.eventos_operacoes")).Number);

        Assert.Equal(50011, (await NegadoAsync(app, $"UPDATE dbo.eventos SET Ativo = 0 WHERE Id = '{evento}'")).Number);
        Assert.Equal(1, await ScalarAsync(app, $"SELECT COUNT(*) FROM dbo.eventos WHERE Id = '{evento}' AND Ativo = 1"));
        await ExecAsync(app, "DECLARE @g uniqueidentifier = NEWID(); EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@g;");
        Assert.Equal(50011, (await NegadoAsync(app, $"UPDATE dbo.eventos SET Ativo = 0 WHERE Id = '{evento}'")).Number);

        await ExecAsync(app, $"""
            DECLARE @g uniqueidentifier = '{membro}';
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@g;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=N'198.51.100.9';
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=N'motivo real';
            """);
        await ExecAsync(app, $"UPDATE dbo.eventos SET Ativo = 0 WHERE Id = '{evento}'");
        Assert.Equal(1, await ScalarAsync(app, $"SELECT COUNT(*) FROM dbo.historico_eventos WHERE EventoId = '{evento}' AND IpResponsavel = '198.51.100.9' AND Motivo = N'motivo real'"));
        Assert.Equal(229, (await NegadoAsync(app, "UPDATE dbo.historico_eventos SET Motivo = N'adulterado'")).Number);
    }

    [Fact]
    public async Task ConcessoesPosterioresEMembershipEmPapeis_NaoAnulamOModelo_EAuditoriaAsDetecta()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        await env.NewManager().InitializeAsync();
        var (evento, _) = await SemearEventoAsync(env);
        await using var app = await env.OpenAppAsync();
        await using var harness = await OpenHarnessAsync(env.Database);

        await ExecAsync(harness, $"GRANT INSERT, UPDATE, DELETE ON dbo.historico_eventos TO [{env.AppUser}]; ALTER ROLE db_datawriter ADD MEMBER [{env.AppUser}];");
        await using var app2 = await env.OpenAppAsync();
        Assert.Equal(229, (await NegadoAsync(app2, "UPDATE dbo.historico_eventos SET Motivo = N'adulterado'")).Number);
        Assert.Equal(229, (await NegadoAsync(app2, "DELETE FROM dbo.historico_eventos")).Number);

        var achados = await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app2);
        Assert.Contains(achados, a => a.Contains("db_datawriter"));

        await env.BootstrapAsync();
        await env.NewManager().InitializeAsync();
        await using var app3 = await env.OpenAppAsync();
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app3));
        Assert.Equal(0, await ScalarAsync(app3, "SELECT ISNULL(IS_MEMBER(N'db_datawriter'), 0)"));
        Assert.Equal(229, (await NegadoAsync(app3, $"DELETE FROM dbo.eventos WHERE Id = '{evento}'")).Number);
    }
}
