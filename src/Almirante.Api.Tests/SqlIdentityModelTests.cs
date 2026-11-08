using System.Text.RegularExpressions;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class SqlIdentityModelTests
{
    private static async Task<SqlIdentityEnvironment> PreparedAsync(bool initialize = true)
    {
        var env = new SqlIdentityEnvironment();
        try
        {
            await env.BootstrapAsync();
            if (initialize) await env.NewManager().InitializeAsync();
            return env;
        }
        catch
        {
            await env.DisposeAsync();
            throw;
        }
    }

    private static async Task<SqlException> DeniedAsync(SqlConnection connection, string sql)
    {
        var ex = await Assert.ThrowsAnyAsync<SqlException>(() => ExecAsync(connection, sql));
        return ex;
    }

    [Fact]
    public async Task Bootstrap_CriaSoUsuariosContidos_SemLoginDeServidor_ESemTrustworthy_EEhIdempotente()
    {
        await using var env = await PreparedAsync(initialize: false);
        var senhaAntiga = env.AdminPassword;

        await env.BootstrapAsync(NewPassword());

        await using var harness = await OpenHarnessAsync(env.Database);
        Assert.Equal(0, await ScalarAsync(harness, $"SELECT COUNT(*) FROM sys.server_principals WHERE name IN (N'{env.AdminUser}', N'{env.AppUser}')"));
        Assert.Equal(2, await ScalarAsync(harness, $"SELECT COUNT(*) FROM sys.database_principals WHERE name IN (N'{env.AdminUser}', N'{env.AppUser}') AND authentication_type = 2"));
        Assert.Equal(1, await ScalarAsync(harness, "SELECT containment FROM sys.databases WHERE name = DB_NAME()"));
        Assert.Equal(0, await ScalarAsync(harness, "SELECT CAST(is_trustworthy_on AS int) FROM sys.databases WHERE name = DB_NAME()"));

        SqlConnection.ClearAllPools();
        var antiga = new SqlConnection(WithAdmin(new SqlConnectionStringBuilder(env.AppCs), env.AdminUser, senhaAntiga));
        Assert.Equal(18456, (await Assert.ThrowsAsync<SqlException>(() => antiga.OpenAsync())).Number);
        await using var admin = await env.OpenAdminAsync();
        Assert.Equal(env.AdminUser, (await ColumnAsync(admin, "SELECT USER_NAME()")).Single());
    }

    [Theory]
    [InlineData("sa", "almirante_user_x", "Tst0123456789abcdefAa1", 50001)]
    [InlineData("almirante_admin_x", "SA", "Tst0123456789abcdefAa1", 50001)]
    [InlineData("mesma_identidade", "mesma_identidade", "Tst0123456789abcdefAa1", 50002)]
    [InlineData("admin;DROP", "almirante_user_x", "Tst0123456789abcdefAa1", 50003)]
    [InlineData("almirante_admin_x", "almirante_user_x", "curta", 50004)]
    public async Task Bootstrap_RecusaSaIdentidadesIguaisNomesInvalidosESenhaCurta(string admin, string app, string password, int erro)
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => RunScriptAsync(new Dictionary<string, string>
        {
            ["DB_NAME"] = $"almirante_recusa_{Guid.NewGuid():N}"[..40], ["ADMIN_USER"] = admin, ["APP_USER"] = app, ["APP_ADMIN_PASSWORD"] = password,
        }));
        Assert.Equal(erro, ex.Number);
    }

    [Fact]
    public async Task Bootstrap_ConverteIdentidadesLegadasBaseadasEmLogin()
    {
        await using var env = new SqlIdentityEnvironment();
        await using (var harness = await OpenHarnessAsync())
        {
            var legado = "Legado" + NewPassword();
            await ExecAsync(harness, $"CREATE DATABASE [{env.Database}]");
            await ExecAsync(harness, $"""
                CREATE LOGIN [{env.AdminUser}] WITH PASSWORD = N'{legado}', CHECK_POLICY = OFF;
                CREATE LOGIN [{env.AppUser}] WITH PASSWORD = N'{legado}', CHECK_POLICY = OFF;
                ALTER SERVER ROLE [sysadmin] ADD MEMBER [{env.AdminUser}];
                """);
            await using var doBanco = await OpenHarnessAsync(env.Database);
            await ExecAsync(doBanco, $"""
                CREATE USER [{env.AdminUser}] FOR LOGIN [{env.AdminUser}];
                CREATE USER [{env.AppUser}] FOR LOGIN [{env.AppUser}];
                ALTER ROLE [db_owner] ADD MEMBER [{env.AdminUser}];
                ALTER ROLE [db_owner] ADD MEMBER [{env.AppUser}];
                GRANT IMPERSONATE ON USER::[dbo] TO [{env.AppUser}];
                """);
        }

        await env.BootstrapAsync();
        await env.NewManager().InitializeAsync();

        await using var verificacao = await OpenHarnessAsync(env.Database);
        Assert.Equal(0, await ScalarAsync(verificacao, $"SELECT COUNT(*) FROM sys.server_principals WHERE name IN (N'{env.AdminUser}', N'{env.AppUser}')"));
        Assert.Equal(0, await ScalarAsync(verificacao,
            $"SELECT COUNT(*) FROM sys.database_role_members m JOIN sys.database_principals u ON u.principal_id = m.member_principal_id JOIN sys.database_principals r ON r.principal_id = m.role_principal_id WHERE u.name = N'{env.AppUser}' AND r.name <> N'{MembroDaRole}'"));
        await using var app = await env.OpenAppAsync();
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));
        await using var admin = await env.OpenAdminAsync();
        Assert.Empty(await DbPrivilegeAuditor.FindAdminExcessPrivilegesAsync(admin));
    }

    private const string MembroDaRole = DbCredentialManager.AppRoleName;

    [Fact]
    public async Task Administrador_NaoEhSaNemSysadminNemTemControlServerOuPapelOuPermissaoDeServidor()
    {
        await using var env = await PreparedAsync(initialize: false);
        await using var admin = await env.OpenAdminAsync();

        Assert.NotEqual("sa", (await ColumnAsync(admin, "SELECT LOWER(SUSER_SNAME())")).Single());
        Assert.False(await DbPrivilegeAuditor.IsSaAsync(admin));
        Assert.Equal(0, await ScalarAsync(admin, "SELECT ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0)"));
        Assert.Equal(0, await ScalarAsync(admin, "SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'CONTROL SERVER'), 0)"));
        foreach (var role in new[] { "sysadmin", "securityadmin", "serveradmin", "setupadmin", "processadmin", "diskadmin", "dbcreator", "bulkadmin" })
            Assert.Equal(0, await ScalarAsync(admin, $"SELECT ISNULL(IS_SRVROLEMEMBER(N'{role}'), 0)"));
        foreach (var permission in DbPrivilegeAuditor.ServerPermissions)
            Assert.Equal(0, await ScalarAsync(admin, $"SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'{permission}'), 0)"));

        Assert.Equal(0, await ScalarAsync(admin, "SELECT ISNULL(IS_MEMBER(N'db_owner'), 0)"));
        Assert.Equal(0, await ScalarAsync(admin, "SELECT ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL'), 0)"));
        Assert.Empty(await DbPrivilegeAuditor.FindAdminExcessPrivilegesAsync(admin));

        Assert.IsType<SqlException>(await DeniedAsync(admin, "ALTER ROLE [db_owner] ADD MEMBER [" + env.AdminUser + "]"));
        try { await ExecAsync(admin, $"GRANT CONTROL ON DATABASE::[{env.Database}] TO [{env.AdminUser}]"); } catch (SqlException) { }
        Assert.Equal(0, await ScalarAsync(admin, "SELECT ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CONTROL'), 0)"));
        await DeniedAsync(admin, "CREATE PROCEDURE dbo.p_escalada WITH EXECUTE AS OWNER AS SELECT 1");
    }

    [Fact]
    public async Task Administrador_NaoAdministraOutrosBancosNemRedefineSenhaDeOutroLogin()
    {
        await using var env = await PreparedAsync(initialize: false);
        var outro = await env.CreateOtherDatabaseAsync("almirante_outro");
        var outroLogin = $"outra_app_{env.Suffix}";
        var outraSenha = NewPassword();
        env.TrackLogin(outroLogin);
        await using (var harness = await OpenHarnessAsync())
        {
            await ExecAsync(harness, $"CREATE LOGIN [{outroLogin}] WITH PASSWORD = N'{outraSenha}', CHECK_POLICY = OFF, DEFAULT_DATABASE = [{outro}]");
        }
        await using (var doOutro = await OpenHarnessAsync(outro))
        {
            await ExecAsync(doOutro, $"CREATE USER [{outroLogin}] FOR LOGIN [{outroLogin}]; CREATE TABLE dbo.Segredo (Id int); INSERT dbo.Segredo VALUES (1); GRANT SELECT ON dbo.Segredo TO [{outroLogin}];");
        }

        await using var admin = await env.OpenAdminAsync();
        await DeniedAsync(admin, $"USE [{outro}]; SELECT * FROM dbo.Segredo");
        await DeniedAsync(admin, $"SELECT * FROM [{outro}].dbo.Segredo");
        await DeniedAsync(admin, $"CREATE TABLE [{outro}].dbo.Invasor (Id int)");
        await DeniedAsync(admin, $"INSERT [{outro}].dbo.Segredo VALUES (2)");
        await DeniedAsync(admin, $"ALTER DATABASE [{outro}] SET READ_ONLY");
        await DeniedAsync(admin, $"DROP DATABASE [{outro}]");
        await DeniedAsync(admin, $"CREATE DATABASE [almirante_invasor_{env.Suffix}]");
        await DeniedAsync(admin, $"CREATE LOGIN [invasor_{env.Suffix}] WITH PASSWORD = N'{NewPassword()}'");
        await DeniedAsync(admin, $"ALTER LOGIN [{outroLogin}] WITH PASSWORD = N'{NewPassword()}'");
        await DeniedAsync(admin, "ALTER LOGIN [sa] WITH PASSWORD = N'" + NewPassword() + "'");
        await DeniedAsync(admin, "EXEC sys.sp_configure N'show advanced options', 1");
        await DeniedAsync(admin, "EXEC master..xp_cmdshell 'echo x'");

        await using var outraApp = new SqlConnection(new SqlConnectionStringBuilder(env.AppCs) { InitialCatalog = outro, UserID = outroLogin, Password = outraSenha }.ConnectionString);
        await outraApp.OpenAsync();
        Assert.Equal(1, await ScalarAsync(outraApp, "SELECT COUNT(*) FROM dbo.Segredo"));
        await using var harnessDepois = await OpenHarnessAsync();
        Assert.Equal(0, await ScalarAsync(harnessDepois, $"SELECT COUNT(*) FROM sys.databases WHERE name = N'almirante_invasor_{env.Suffix}'"));
    }

    [Fact]
    public async Task Migrations_ExecutamComAdministradorDedicado_SemDbOwnerNemSysadmin()
    {
        await using var env = await PreparedAsync();

        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(env.AdminCs).Options;
        await using var db = new AlmiranteDbContext(options);
        var aplicadas = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.NotEmpty(aplicadas);
        Assert.Equal(db.Database.GetMigrations().ToList(), aplicadas);

        await using var harness = await OpenHarnessAsync(env.Database);
        Assert.Equal(0, await ScalarAsync(harness, "SELECT COUNT(*) FROM sys.objects WHERE type IN ('U','TR') AND principal_id IS NOT NULL"));
        Assert.True(await ScalarAsync(harness, "SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_Lancamentos_AuditoriaExclusaoLogica'") > 0);
    }

    [Fact]
    public async Task Runtime_NaoEhDbOwnerNemTemDdlNemAlterAnyLogin_NaoApagaDadosNemEscreveNaAuditoria()
    {
        await using var env = await PreparedAsync();
        await using var app = await env.OpenAppAsync();

        Assert.Equal(env.AppUser, (await ColumnAsync(app, "SELECT USER_NAME()")).Single());
        Assert.Equal(0, await ScalarAsync(app, "SELECT ISNULL(IS_MEMBER(N'db_owner'), 0)"));
        Assert.Equal(1, await ScalarAsync(app, $"SELECT ISNULL(IS_MEMBER(N'{MembroDaRole}'), 0)"));
        Assert.Equal(0, await ScalarAsync(app, "SELECT ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0)"));
        Assert.Equal(0, await ScalarAsync(app, "SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY LOGIN'), 0)"));
        Assert.Equal(0, await ScalarAsync(app, "SELECT ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0)"));
        Assert.Equal(0, await ScalarAsync(app, "SELECT ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, N'CONTROL SERVER'), 0)"));
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));

        await DeniedAsync(app, "CREATE TABLE dbo.Invasora (Id int)");
        await DeniedAsync(app, "ALTER TABLE dbo.Cargos ADD Nova int");
        await DeniedAsync(app, $"CREATE LOGIN [invasor_{env.Suffix}] WITH PASSWORD = N'{NewPassword()}'");
        await DeniedAsync(app, "ALTER LOGIN [sa] DISABLE");
        await DeniedAsync(app, "CREATE USER [invasor] WITHOUT LOGIN");
        await DeniedAsync(app, "EXECUTE AS USER = 'dbo'");

        foreach (var tabela in new[] { "Cargos", "Usuarios", "Lancamentos", "RefreshTokens" })
            Assert.Equal(229, (await DeniedAsync(app, $"DELETE FROM dbo.[{tabela}]")).Number);
        await ExecAsync(app, "DELETE FROM dbo.AuthSessions WHERE 1 = 0");

        Assert.Equal(229, (await DeniedAsync(app, "INSERT INTO dbo.lancamentos_deletados (Id) VALUES (NEWID())")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "UPDATE dbo.lancamentos_deletados SET Motivo = N'x'")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "DELETE FROM dbo.lancamentos_deletados")).Number);
        await ScalarAsync(app, "SELECT COUNT(*) FROM dbo.lancamentos_deletados");

        Assert.Equal(229, (await DeniedAsync(app, "INSERT INTO dbo.historico_eventos (Id) VALUES (NEWID())")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "UPDATE dbo.historico_eventos SET Motivo = N'x'")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "DELETE FROM dbo.historico_eventos")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "DELETE FROM dbo.eventos")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "DELETE FROM dbo.evento_membros")).Number);
        await ScalarAsync(app, "SELECT COUNT(*) FROM dbo.historico_eventos");

        Assert.Equal(229, (await DeniedAsync(app, "INSERT INTO dbo._usuarios_hist (Id) VALUES (NEWID())")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "UPDATE dbo._usuarios_hist SET Nome = N'x'")).Number);
        Assert.Equal(229, (await DeniedAsync(app, "DELETE FROM dbo._usuarios_hist")).Number);
        await ScalarAsync(app, "SELECT COUNT(*) FROM dbo._usuarios_hist");
    }

    [Fact]
    public async Task Runtime_PermissoesEfetivasPorTabela_SaoExatamenteAsPrevistas()
    {
        await using var env = await PreparedAsync();
        await using var app = await env.OpenAppAsync();

        var tabelas = await ColumnAsync(app, "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo')");
        Assert.NotEmpty(tabelas);
        foreach (var tabela in tabelas)
        {
            var efetivas = new List<string>();
            foreach (var permissao in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "ALTER", "CONTROL", "REFERENCES", "TAKE OWNERSHIP" })
                if (await ScalarAsync(app, $"SELECT ISNULL(HAS_PERMS_BY_NAME(N'dbo.[{tabela}]', N'OBJECT', N'{permissao}'), 0)") == 1)
                    efetivas.Add(permissao);

            var esperadas = tabela switch
            {
                DbPrivilegeAuditor.AuditTable or DbPrivilegeAuditor.EventosAuditTable or DbPrivilegeAuditor.UsuariosAuditTable
                    or DbPrivilegeAuditor.FormacaoAuditTable or DbPrivilegeAuditor.AlmoxarifadoAuditTable or "__EFMigrationsHistory" => new[] { "SELECT" },
                DbPrivilegeAuditor.SessionsTable => ["SELECT", "INSERT", "UPDATE", "DELETE"],
                _ when DbPrivilegeAuditor.InsertOnlyTables.Contains(tabela) => ["SELECT", "INSERT"],
                _ => ["SELECT", "INSERT", "UPDATE"],
            };
            Assert.True(esperadas.OrderBy(x => x).SequenceEqual(efetivas.OrderBy(x => x)), $"{tabela}: esperado [{string.Join(",", esperadas)}], obtido [{string.Join(",", efetivas)}]");
        }
    }

    [Fact]
    public async Task Runtime_ExecutaAsOperacoesNormaisDoEntityFramework()
    {
        await using var env = await PreparedAsync();
        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(env.AppCs)
            .AddInterceptors(new AppDbCredentialInterceptor(env.Provider, TimeProvider.System)).Options;
        await using var db = new AlmiranteDbContext(options);

        await DbSeeder.SeedAsync(db, new PasswordHasher<Usuario>(),
            Microsoft.Extensions.Options.Options.Create(new SeedOptions { Nome = "Admin", Email = "admin@local.dev", Senha = "Tst-Senha-Longa-2026-ok-Xz9" }));

        var lancamento = await db.Lancamentos.FirstAsync();
        lancamento.Descricao = "atualizado pelo runtime";
        await db.SaveChangesAsync();
        Assert.Equal("atualizado pelo runtime", await db.Lancamentos.Where(l => l.Id == lancamento.Id).Select(l => l.Descricao).SingleAsync());

        Assert.Equal(0, await db.AuthSessions.Where(s => s.AbsoluteExpiresAtUtc < DateTime.UtcNow.AddYears(-1)).ExecuteDeleteAsync());
        Assert.NotEmpty(await db.Cargos.ToListAsync());
    }

    [Fact]
    public async Task Rotacao_TrocaASenha_InvalidaAAnteriorParaNovasConexoesMesmoComPoolAquecido_ESegundaRotacaoTambemFunciona()
    {
        await using var env = await PreparedAsync();
        var manager = env.NewManager();
        var primeira = env.Provider.Current!;
        var senhaPrimeira = env.CurrentAppPassword();

        await using (var aquecida = new SqlConnection(env.AppCs, primeira))
        {
            await aquecida.OpenAsync();
        }

        await manager.RotateAsync();
        var segunda = env.Provider.Current!;
        var senhaSegunda = env.CurrentAppPassword();
        Assert.NotEqual(senhaPrimeira, senhaSegunda);
        Assert.NotSame(primeira, segunda);

        await using (var nova = new SqlConnection(env.AppCs, segunda)) { await nova.OpenAsync(); }
        var velha = new SqlConnection(env.AppCs, primeira);
        Assert.Equal(18456, (await Assert.ThrowsAsync<SqlException>(() => velha.OpenAsync())).Number);

        await manager.RotateAsync();
        var terceira = env.Provider.Current!;
        Assert.NotEqual(senhaSegunda, env.CurrentAppPassword());
        await using (var maisNova = new SqlConnection(env.AppCs, terceira)) { await maisNova.OpenAsync(); }
        var anterior = new SqlConnection(env.AppCs, segunda);
        Assert.Equal(18456, (await Assert.ThrowsAsync<SqlException>(() => anterior.OpenAsync())).Number);

        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(env.AppCs).AddInterceptors(new AppDbCredentialInterceptor(env.Provider, TimeProvider.System)).Options;
        await using var db = new AlmiranteDbContext(options);
        Assert.True(await db.Database.CanConnectAsync());
        await manager.RotateAsync();
        await using var db2 = new AlmiranteDbContext(options);
        Assert.Equal(0, await db2.Cargos.CountAsync());
    }

    [Fact]
    public async Task Rotacao_NaoExpoeSenhaEmLogNemEmExcecao()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        var logs = new CapturingLoggerFactory();
        var manager = env.NewManager(logs);

        await manager.InitializeAsync();
        await manager.RotateAsync();

        var senhaAdmin = env.AdminPassword;
        var senhaRuntime = env.CurrentAppPassword();
        var tudo = string.Join("\n", logs.Entries);
        Assert.NotEmpty(logs.Entries);
        NoSecret.Assert(tudo, senhaAdmin);
        NoSecret.Assert(tudo, senhaRuntime);
        Assert.DoesNotMatch(new Regex("[A-Za-z0-9]{40,}"), tudo);

        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"ALTER ROLE [{MembroDaRole}] DROP MEMBER [{env.AppUser}]; DROP USER [{env.AppUser}];");
        }
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RotateAsync());
        NoSecret.Assert(ex.ToString(), senhaAdmin);
        NoSecret.Assert(ex.ToString(), senhaRuntime);
        Assert.DoesNotMatch(new Regex("[A-Za-z0-9]{40,}"), ex.ToString());
        Assert.Null(ex.InnerException);
        Assert.Contains("bootstrap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(senhaRuntime, env.CurrentAppPassword());
    }

    [Theory]
    [InlineData("SERVER ROLE sysadmin", "papel de servidor sysadmin")]
    [InlineData("SERVER ROLE securityadmin", "papel de servidor securityadmin")]
    [InlineData("SERVER ROLE serveradmin", "papel de servidor serveradmin")]
    [InlineData("SERVER ROLE dbcreator", "papel de servidor dbcreator")]
    [InlineData("PERMISSION CONTROL SERVER", "permissão de servidor CONTROL SERVER")]
    [InlineData("PERMISSION ALTER ANY LOGIN", "permissão de servidor ALTER ANY LOGIN")]
    [InlineData("PERMISSION ALTER ANY SERVER ROLE", "permissão de servidor ALTER ANY SERVER ROLE")]
    [InlineData("PERMISSION IMPERSONATE ANY LOGIN", "permissão de servidor IMPERSONATE ANY LOGIN")]
    [InlineData("PERMISSION ALTER ANY DATABASE", "permissão de servidor ALTER ANY DATABASE")]
    [InlineData("PERMISSION CREATE ANY DATABASE", "permissão de servidor CREATE ANY DATABASE")]
    public async Task Startup_RecusaAdministradorComPrivilegioDeServidor_AntesDeAlterarQualquerCoisa(string concessao, string achado)
    {
        await using var env = await PreparedAsync(initialize: false);
        var login = $"admin_perigoso_{env.Suffix}";
        var senha = NewPassword();
        env.TrackLogin(login);
        await using (var harness = await OpenHarnessAsync())
        {
            await ExecAsync(harness, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{senha}', CHECK_POLICY = OFF");
            await ExecAsync(harness, concessao.StartsWith("SERVER ROLE", StringComparison.Ordinal)
                ? $"ALTER SERVER ROLE [{concessao["SERVER ROLE ".Length..]}] ADD MEMBER [{login}]"
                : $"GRANT {concessao["PERMISSION ".Length..]} TO [{login}]");
        }
        await using (var doBanco = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(doBanco, $"CREATE USER [{login}] FOR LOGIN [{login}]");
        }
        var adminCs = WithAdmin(new SqlConnectionStringBuilder(env.AppCs), login, senha);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => env.NewManager(adminCs: adminCs).InitializeAsync());

        Assert.Contains(achado, ex.Message);
        NoSecret.Assert(ex.ToString(), senha);
        await using var verificacao = await OpenHarnessAsync(env.Database);
        Assert.Equal(0, await ScalarAsync(verificacao, "SELECT COUNT(*) FROM sys.tables"));
        Assert.Null(env.Provider.Current);
    }

    [Fact]
    public async Task Startup_RecusaAdministradorLegadoComDbOwner_ETrustworthy_EORebootstrapCorrige()
    {
        await using var env = await PreparedAsync(initialize: false);
        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"ALTER ROLE [db_owner] ADD MEMBER [{env.AdminUser}]; ALTER ROLE [db_ddladmin] ADD MEMBER [{env.AdminUser}]");
        }
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => env.NewManager().InitializeAsync());
        Assert.Contains("papel de banco db_owner", ex.Message);
        Assert.Contains("papel de banco db_ddladmin", ex.Message);

        await env.BootstrapAsync(env.AdminPassword);
        await using (var harness = await OpenHarnessAsync())
        {
            await ExecAsync(harness, $"ALTER DATABASE [{env.Database}] SET TRUSTWORTHY ON");
        }
        var trustworthy = await Assert.ThrowsAsync<InvalidOperationException>(() => env.NewManager().InitializeAsync());
        Assert.Contains("TRUSTWORTHY", trustworthy.Message);

        await env.BootstrapAsync(env.AdminPassword);
        await env.NewManager().InitializeAsync();
    }

    [Fact]
    public async Task Startup_SemBootstrap_FalhaComMensagemClara_SemFallbackParaSa()
    {
        await using var env = new SqlIdentityEnvironment();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => env.NewManager().InitializeAsync());

        Assert.Contains("bootstrap", ex.Message, StringComparison.OrdinalIgnoreCase);
        NoSecret.Assert(ex.ToString(), env.AdminPassword);
        Assert.Null(env.Provider.Current);
    }

    [Fact]
    public async Task Startup_RecusaRuntimeComPrivilegioAlemDoPrevisto_ERebootstrapNormaliza()
    {
        await using var env = await PreparedAsync();

        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"GRANT IMPERSONATE ON USER::[dbo] TO [{env.AppUser}]; GRANT DELETE ON OBJECT::dbo.Lancamentos TO [{env.AppUser}];");
        }
        var auditado = await Assert.ThrowsAsync<InvalidOperationException>(() => env.NewManager().InitializeAsync());
        Assert.Contains("IMPERSONATE", auditado.Message);
        Assert.Contains("DELETE em Lancamentos", auditado.Message);

        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"ALTER ROLE [db_owner] ADD MEMBER [{env.AppUser}];");
        }
        var bloqueado = await Assert.ThrowsAsync<InvalidOperationException>(() => env.NewManager().InitializeAsync());
        Assert.Contains("bootstrap", bloqueado.Message);

        await env.BootstrapAsync(env.AdminPassword);
        await env.NewManager().InitializeAsync();
        await using var app = await env.OpenAppAsync();
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));
    }

    [Fact]
    public async Task VerificacaoDePrivilegio_RuntimeMinimaPassa_EIdentidadeAmplaSoEhRecusadaEmEnforce()
    {
        await using var env = await PreparedAsync();
        static IConfiguration Modo(string modo) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [DbPrivilegeCheck.SettingName] = modo }).Build();
        var logs = new CapturingLoggerFactory();

        // Como no startup sem DbCredentials, a verificação abre a conexão do contexto por conta própria: a credencial já vem nela.
        await using var conexaoMinima = new SqlConnection(env.AppCs, env.Provider.Current);
        await using (var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(conexaoMinima).Options))
        {
            await DbPrivilegeCheck.RunAsync(db, Modo("Enforce"), logs.CreateLogger("check"));
        }

        await using (var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(env.AdminCs).Options))
        {
            await DbPrivilegeCheck.RunAsync(db, Modo("Off"), logs.CreateLogger("check"));
            Assert.Empty(logs.Entries);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DbPrivilegeCheck.RunAsync(db, Modo("Enforce"), logs.CreateLogger("check")));
            Assert.Contains("privilégios além do mínimo", ex.Message);
            NoSecret.Assert(ex.ToString(), env.AdminPassword);
        }

        Assert.Throws<InvalidOperationException>(() => DbPrivilegeCheck.ParseMode("Talvez", DbPrivilegeCheck.SettingName));
    }

    [Fact]
    public async Task Startup_RecusaSaComoIdentidadeAdministrativa_QuandoOHarnessEhSa()
    {
        var harness = new SqlConnectionStringBuilder(HarnessConnectionString);
        if (!SqlIdentityPolicy.IsSaName(harness.UserID)) return;

        await using var env = await PreparedAsync(initialize: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => env.NewManager(adminCs: harness.ConnectionString.WithDatabase(env.Database)).InitializeAsync());
        Assert.Contains("sa", ex.Message, StringComparison.OrdinalIgnoreCase);
        NoSecret.Assert(ex.ToString(), harness.Password);

        await using var sa = new SqlConnection(harness.ConnectionString);
        await sa.OpenAsync();
        Assert.True(await DbPrivilegeAuditor.IsSaAsync(sa));
        Assert.Contains("identidade sa", await DbPrivilegeAuditor.FindAdminExcessPrivilegesAsync(sa));
        Assert.Contains("identidade sa", await DbPrivilegeAuditor.FindExcessPrivilegesAsync(sa));

        var options = new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(harness.ConnectionString).Options;
        await using var db = new AlmiranteDbContext(options);
        foreach (var modo in new[] { "Off", "Warn", "Enforce" })
        {
            var configuracao = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [DbPrivilegeCheck.SettingName] = modo }).Build();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DbPrivilegeCheck.RunAsync(db, configuracao, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        }
    }
}

internal static class ConnectionStringExtensions
{
    public static string WithDatabase(this string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;
}
