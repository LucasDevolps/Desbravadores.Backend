using Almirante.Api.Data;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Almirante.Api.Tests;

// Menor privilégio do usuário SQL da aplicação e integridade da auditoria de exclusões, contra SQL Server
// real (bloqueadores 3 e 4 da validação da PR #59). Banco e login descartáveis, removidos ao final.
[Trait("Category", "RequiresSqlServer")]
public sealed class DbPrivilegeTests : IAsyncLifetime
{
    private readonly string _server = Environment.GetEnvironmentVariable(SqlServerApiFactory.EnvironmentVariable)
        ?? throw new InvalidOperationException($"Defina {SqlServerApiFactory.EnvironmentVariable}.");
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private string Database => $"almirante_priv_{_suffix}";
    private string AppUser => $"almirante_user_{_suffix}";
    private string AdminCs => new SqlConnectionStringBuilder(_server) { InitialCatalog = Database }.ConnectionString;
    private string AppCs
    {
        get
        {
            var admin = new SqlConnectionStringBuilder(AdminCs);
            return new SqlConnectionStringBuilder
            {
                DataSource = admin.DataSource, InitialCatalog = Database,
                TrustServerCertificate = admin.TrustServerCertificate, Encrypt = admin.Encrypt,
            }.ConnectionString;
        }
    }

    private readonly AppDbCredentialProvider _provider = new();

    private DbCredentialManager NewManager() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:almirante"] = AppCs,
            ["ConnectionStrings:AlmiranteAdmin"] = AdminCs,
        }).Build(),
        Microsoft.Extensions.Options.Options.Create(new DbCredentialOptions { AppUser = AppUser }), _provider, NullLogger<DbCredentialManager>.Instance);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, $"""
            IF DB_ID(N'{Database}') IS NOT NULL BEGIN ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; END
            IF SUSER_ID(N'{AppUser}') IS NOT NULL DROP LOGIN [{AppUser}];
            """);
    }

    private async Task<SqlConnection> OpenAppAsync()
    {
        var connection = new SqlConnection(AppCs, _provider.Current);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<SqlConnection> OpenAdminAsync()
    {
        var connection = new SqlConnection(AdminCs);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<Guid> GuidAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task SeedAsync()
    {
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(AdminCs).Options;
        await using var db = new AlmiranteDbContext(options);
        await DbSeeder.SeedAsync(db, new PasswordHasher<Almirante.Api.Entities.Usuario>(),
            Microsoft.Extensions.Options.Options.Create(new SeedOptions { Nome = "Admin", Email = "admin@local.dev", Senha = "Tst-Senha-Longa-2026-ok" }));
    }

    [Fact]
    public async Task UsuarioProvisionadoPorVersaoAnterior_ComDbOwner_TemOPrivilegioRemovidoNoStartup()
    {
        await NewManager().InitializeAsync();

        // Estado legado descrito no relatório: login já existente e associado a db_owner.
        await using (var admin = await OpenAdminAsync())
        {
            await ExecAsync(admin, $"ALTER ROLE [db_owner] ADD MEMBER [{AppUser}]; ALTER ROLE [db_datawriter] ADD MEMBER [{AppUser}]; GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [{AppUser}];");
        }

        await NewManager().InitializeAsync();

        await using var app = await OpenAppAsync();
        Assert.Equal(0, await ScalarAsync(app, "SELECT IS_ROLEMEMBER('db_owner')"));
        Assert.Equal(0, await ScalarAsync(app, "SELECT IS_ROLEMEMBER('db_datawriter')"));
        Assert.Equal(0, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME('dbo.Lancamentos','OBJECT','DELETE')"));
        Assert.Equal(0, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE')"));
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));
    }

    // IMPERSONATE é permissão de classe 4 (sobre outro principal do banco), não aparece em nenhuma
    // checagem de papel/objeto/esquema. Com IMPERSONATE em dbo, um EXECUTE AS USER dá db_owner
    // efetivo — e a versão anterior do provisionamento filtrava p.class IN (0,1,3), então o GRANT
    // sobrevivia a todo startup e a auditoria dizia que estava tudo certo.
    [Fact]
    public async Task ImpersonateEmDbo_ERevogadoNoStartup_EDetectadoPelaAuditoria()
    {
        await NewManager().InitializeAsync();

        await using (var admin = await OpenAdminAsync())
        {
            await ExecAsync(admin, $"GRANT IMPERSONATE ON USER::[dbo] TO [{AppUser}];");
        }

        // Antes da normalização: a permissão existe e a auditoria precisa enxergá-la.
        await using (var comprometido = await OpenAppAsync())
        {
            Assert.Equal(1, await ScalarAsync(comprometido, "SELECT HAS_PERMS_BY_NAME('dbo','USER','IMPERSONATE')"));
            Assert.Contains(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(comprometido),
                f => f.Contains("IMPERSONATE", StringComparison.OrdinalIgnoreCase));
        }

        SqlConnection.ClearAllPools();
        await NewManager().InitializeAsync();

        await using var app = await OpenAppAsync();
        Assert.Equal(0, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME('dbo','USER','IMPERSONATE')"));
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));
    }

    // A conexão administrativa precisa de DDL e ALTER ANY LOGIN, mas não de sysadmin: ela vive no
    // processo da API, então sysadmin aqui transforma qualquer leitura do ambiente do container em
    // comprometimento do servidor inteiro. Warn (padrão) só registra; Enforce recusa iniciar.
    [Fact]
    public async Task ConexaoAdministrativaSysadmin_EDetectada_EEnforceRecusaIniciar()
    {
        await NewManager().InitializeAsync();

        await using var admin = await OpenAdminAsync();
        var achados = await DbPrivilegeAuditor.FindAdminExcessPrivilegesAsync(admin);

        // O ALMIRANTE_TEST_SQLSERVER do ambiente de teste normalmente é sysadmin (sa ou Windows auth
        // local). Se for, o Enforce tem de barrar; se o ambiente já usar um login restrito, o Warn
        // não tem nada a relatar — os dois casos são válidos, e cada um verifica um lado da política.
        var configuracao = (string modo) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [DbAdminPrivilegeCheck.SettingName] = modo }).Build();

        if (achados.Count > 0)
        {
            Assert.Contains(achados, f => f.Contains("sysadmin", StringComparison.OrdinalIgnoreCase)
                                       || f.Contains("CONTROL SERVER", StringComparison.OrdinalIgnoreCase));

            var excecao = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DbAdminPrivilegeCheck.RunAsync(admin, configuracao("Enforce"), NullLogger.Instance));
            Assert.Contains("criar-usuario-admin-app.sql", excecao.Message);

            // Warn não derruba o startup (ambientes existentes ainda usam sa).
            await DbAdminPrivilegeCheck.RunAsync(admin, configuracao("Warn"), NullLogger.Instance);
        }
        else
        {
            await DbAdminPrivilegeCheck.RunAsync(admin, configuracao("Enforce"), NullLogger.Instance);
        }

        // Off nunca falha, qualquer que seja a identidade.
        await DbAdminPrivilegeCheck.RunAsync(admin, configuracao("Off"), NullLogger.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DbAdminPrivilegeCheck.RunAsync(admin, configuracao("ModoInexistente"), NullLogger.Instance));
    }

    [Fact]
    public async Task LoginNovoELoginCorrigido_TemAsMesmasPermissoesEfetivas()
    {
        await NewManager().InitializeAsync();
        var permissoesNovo = await EffectivePermissionsAsync();

        await using (var admin = await OpenAdminAsync())
        {
            await ExecAsync(admin, $"ALTER ROLE [db_owner] ADD MEMBER [{AppUser}];");
        }
        await NewManager().InitializeAsync();

        Assert.Equal(permissoesNovo, await EffectivePermissionsAsync());
    }

    private async Task<string> EffectivePermissionsAsync()
    {
        await using var app = await OpenAppAsync();
        await using var command = app.CreateCommand();
        command.CommandText = """
            SELECT t.name + ':' + STRING_AGG(p.perm, ',') WITHIN GROUP (ORDER BY p.perm)
            FROM sys.tables t
            CROSS APPLY (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('ALTER'), ('CONTROL')) p(perm)
            WHERE HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), 'OBJECT', p.perm) = 1
            GROUP BY t.name
            ORDER BY t.name
            """;
        var linhas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) linhas.Add(reader.GetString(0));
        return string.Join("|", linhas);
    }

    [Fact]
    public async Task Auditoria_AplicacaoNaoFalsificaNemAlteraHistorico_MasTriggerContinuaGravando()
    {
        await NewManager().InitializeAsync();
        await SeedAsync();

        Guid usuarioId, lancamentoId;
        await using (var admin = await OpenAdminAsync())
        {
            usuarioId = await GuidAsync(admin, "SELECT TOP 1 Id FROM Usuarios");
            lancamentoId = await GuidAsync(admin, "SELECT TOP 1 Id FROM Lancamentos WHERE Ativo = 1");
        }

        await using var app = await OpenAppAsync();

        // Escrita direta na auditoria (fabricar/alterar/apagar) precisa ser negada (229 = permission denied).
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

        // Exclusão legítima: o trigger (cadeia de propriedade) grava mesmo sem INSERT direto para a aplicação.
        await ExecAsync(app, $"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value='{usuarioId}';
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='198.51.100.23';
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=N'duplicado';
            UPDATE dbo.Lancamentos SET Ativo = 0 WHERE Id = '{lancamentoId}';
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=NULL;
            """);

        await using var verificacao = await OpenAdminAsync();
        Assert.Equal(1, await ScalarAsync(verificacao, "SELECT COUNT(*) FROM lancamentos_deletados"));
        Assert.Equal(1, await ScalarAsync(verificacao,
            $"SELECT COUNT(*) FROM lancamentos_deletados WHERE LancamentoId = '{lancamentoId}' AND UsuarioResponsavelId = '{usuarioId}' AND IpResponsavel = '198.51.100.23' AND Motivo = N'duplicado'"));
    }

    [Fact]
    public async Task Startup_RecusaIdentidadeComPapelDeServidor()
    {
        await NewManager().InitializeAsync();
        await using (var admin = await OpenAdminAsync())
        {
            await ExecAsync(admin, $"ALTER SERVER ROLE [dbcreator] ADD MEMBER [{AppUser}];");
        }

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewManager().InitializeAsync());
            Assert.Contains("papel de servidor dbcreator", ex.Message);
            Assert.DoesNotContain("PASSWORD", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var admin = await OpenAdminAsync();
            await ExecAsync(admin, $"ALTER SERVER ROLE [dbcreator] DROP MEMBER [{AppUser}];");
        }
    }
}
