using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Almirante.Api.Tests;

// Migration AddUsuariosDadosPessoais sobre um banco já populado (usuários e _usuarios_hist gravados pela trigger
// ANTERIOR): preserva tudo, deixa os campos novos NULL, recusa e-mails incompatíveis com varchar(100) ASCII sem alterar
// nada e tem rollback estrutural coerente. Mesmo desenho de LancamentosAuditMigrationTests.
[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosDadosPessoaisMigrationTests
{
    private const string MigrationAnterior = "20260929213958_AddUsuariosAtivoEHistorico";
    private const string MigrationEmTeste = "20261001112715_AddUsuariosDadosPessoais";

    private static AlmiranteDbContext Contexto(SqlServerApiFactory factory) =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(factory.ConnectionString).Options);

    private static async Task<Cargo> SemearCargosAsync(AlmiranteDbContext db)
    {
        var ds = new Cargo { Id = Guid.NewGuid(), Nome = "Desbravador", Descricao = "Membro do clube.", CriadoPor = "Teste", Role = "DS" };
        db.Cargos.AddRange(ds,
            new Cargo { Id = Guid.NewGuid(), Nome = "Diretor", Descricao = "Diretoria do clube.", CriadoPor = "Teste", Role = "DIR" },
            new Cargo { Id = Guid.NewGuid(), Nome = "Administrador", Descricao = "Acesso administrativo.", CriadoPor = "Teste", Role = "ADM" });
        await db.SaveChangesAsync();
        return ds;
    }

    private static async Task<Usuario> InserirLegadoAsync(AlmiranteDbContext db, Guid cargoId, string email, string nome = "Legado")
    {
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(), Nome = nome, Email = email, EmailNormalizado = email.ToUpperInvariant(), SenhaHash = string.Empty,
            CargoId = cargoId, DataCriacao = DateTime.UtcNow.AddDays(-300),
        };
        usuario.SenhaHash = new PasswordHasher<Usuario>().HashPassword(usuario, TestHelpers.SenhaPadraoTeste);
        await TestHelpers.InserirUsuarioLegadoAsync(db, usuario);
        return usuario;
    }

    // Alteração cadastral com contexto de auditoria: a trigger vigente grava o histórico (formato da época).
    private static Task AlterarNomeAuditadoAsync(AlmiranteDbContext db, Guid id, Guid responsavel, string nome) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={responsavel};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value={"198.51.100.1"};
            UPDATE dbo.Usuarios SET Nome = {nome} WHERE Id = {id};
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
            """);

    private static async Task<string?> EscalarAsync(AlmiranteDbContext db, string sql)
    {
        var conexao = db.Database.GetDbConnection();
        var abriu = conexao.State != System.Data.ConnectionState.Open;
        if (abriu) await conexao.OpenAsync();
        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText = sql;
            var valor = await comando.ExecuteScalarAsync();
            return valor is null or DBNull ? null : Convert.ToString(valor);
        }
        finally
        {
            if (abriu) await conexao.CloseAsync();
        }
    }

    private static Task<string?> TipoAsync(AlmiranteDbContext db, string tabela, string coluna) => EscalarAsync(db, $"""
        SELECT t.name + '(' + CAST(c.max_length AS varchar(10)) + ')' FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID('dbo.{tabela}') AND c.name = '{coluna}'
        """);

    [Fact]
    public async Task Migration_PreservaUsuariosEHistoricoLegados_ComCamposNovosNull_ELoginPorEmailContinua()
    {
        using var factory = new SqlServerApiFactory();
        Usuario legado, limite;
        await using (var db = Contexto(factory))
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(MigrationAnterior);
            var cargo = await SemearCargosAsync(db);

            legado = await InserirLegadoAsync(db, cargo.Id, "Legado.Pessoal@Local.Dev");
            // Exatamente 100 caracteres: o limite novo, aceito sem truncar.
            var emailLimite = new string('a', 100 - "@local.dev".Length) + "@local.dev";
            limite = await InserirLegadoAsync(db, cargo.Id, emailLimite, "Limite");
            Assert.Equal(100, emailLimite.Length);

            // Duas versões no histórico, gravadas pela trigger ANTERIOR (sem colunas de dados pessoais).
            await AlterarNomeAuditadoAsync(db, legado.Id, limite.Id, "Legado v2");
            await AlterarNomeAuditadoAsync(db, legado.Id, limite.Id, "Legado v3");

            await migrator.MigrateAsync();
        }

        await using (var db = Contexto(factory))
        {
            Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());

            var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.Id == legado.Id || u.Id == limite.Id).ToListAsync();
            Assert.Equal(2, usuarios.Count);
            var salvo = usuarios.Single(u => u.Id == legado.Id);
            Assert.Equal("Legado v3", salvo.Nome);
            Assert.Equal("Legado.Pessoal@Local.Dev", salvo.Email);              // caixa e conteúdo preservados
            Assert.Equal("LEGADO.PESSOAL@LOCAL.DEV", salvo.EmailNormalizado);
            Assert.Equal(limite.Email, usuarios.Single(u => u.Id == limite.Id).Email);
            Assert.All(usuarios, u =>
            {
                Assert.Null(u.Cpf);
                Assert.Null(u.DataNascimento);
                Assert.Null(u.Telefone);
                Assert.True(u.Ativo);
            });

            var historico = await db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == legado.Id).OrderBy(h => h.AlteradoEmUtc).ToListAsync();
            Assert.Equal(2, historico.Count);
            Assert.Equal(new[] { "Legado", "Legado v2" }, historico.Select(h => h.Nome).Order()); // podem empatar em AlteradoEmUtc
            Assert.All(historico, h =>
            {
                Assert.Equal("Legado.Pessoal@Local.Dev", h.Email);
                Assert.Null(h.Cpf);                 // não preenchido com dado atual
                Assert.Null(h.DataNascimento);
                Assert.Null(h.Telefone);
                Assert.Equal(limite.Id, h.UsuarioResponsavelId);
                Assert.Equal("198.51.100.1", h.IpResponsavel);
            });

            Assert.Equal("varchar(100)", await TipoAsync(db, "Usuarios", "Email"));
            Assert.Equal("varchar(100)", await TipoAsync(db, "Usuarios", "EmailNormalizado"));
            Assert.Equal("1", await EscalarAsync(db, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Usuarios_EmailNormalizado' AND is_unique = 1"));
            Assert.Equal("1", await EscalarAsync(db, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_Usuarios_Cpf' AND is_unique = 1 AND has_filter = 1"));
        }

        // A aplicação sobe sobre o mesmo banco (migrations já aplicadas, seed idempotente): o login por e-mail do usuário
        // legado continua (sem diferenciar caixa) e um PUT acrescenta CPF, auditando o estado anterior com CPF NULL.
        using var cliente = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(cliente);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(cliente, "legado.pessoal@local.dev", TestHelpers.SenhaPadraoTeste)).StatusCode);

        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var cargoDs = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Id == legado.Id).Select(u => u.CargoId).SingleAsync());
        var put = await admin.PutAsJsonAsync($"/api/Usuarios/{legado.Id}", new
        {
            nome = "Legado v3", email = legado.Email, cargoId = cargoDs, cpf = "012.345.678-90", dataNascimento = "1960-06-01", telefone = "(61) 3333-0000",
        });
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var atual = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == legado.Id));
        Assert.Equal("01234567890", atual.Cpf);
        var ultimo = await TestHelpers.WithDbAsync(factory, db =>
            db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == legado.Id).OrderByDescending(h => h.AlteradoEmUtc).FirstAsync());
        Assert.Equal("Legado v3", ultimo.Nome);
        Assert.Null(ultimo.Cpf);
        Assert.Null(ultimo.DataNascimento);
        Assert.Null(ultimo.Telefone);
    }

    [Fact]
    public async Task Migration_EmailIncompativelComVarchar100Ascii_InterrompeComDiagnostico_SemAlterarNada()
    {
        using var factory = new SqlServerApiFactory();
        await using var db = Contexto(factory);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationAnterior);
        var cargo = await SemearCargosAsync(db);

        var longo = await InserirLegadoAsync(db, cargo.Id, new string('b', 101 - "@local.dev".Length) + "@local.dev", "Longo");
        var acentuado = await InserirLegadoAsync(db, cargo.Id, "joão.silva@local.dev", "Acentuado");
        var valido = await InserirLegadoAsync(db, cargo.Id, "valido@local.dev", "Válido");

        var ex = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync());

        Assert.Equal(50031, ex.Number);
        Assert.Contains("2 usuario(s)", ex.Message);
        Assert.Contains(longo.Id.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(acentuado.Id.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(valido.Id.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("joão", ex.Message);  // diagnóstico sem dado pessoal

        // Nada foi aplicado: nem a migration, nem a redução da coluna, nem colunas novas; e-mails intactos.
        Assert.DoesNotContain(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal("nvarchar(512)", await TipoAsync(db, "Usuarios", "Email"));
        Assert.Null(await TipoAsync(db, "Usuarios", "Cpf"));
        Assert.Equal("joão.silva@local.dev", await EscalarAsync(db, $"SELECT Email FROM dbo.Usuarios WHERE Id = '{acentuado.Id}'"));
        Assert.Equal(longo.Email, await EscalarAsync(db, $"SELECT Email FROM dbo.Usuarios WHERE Id = '{longo.Id}'"));
    }

    [Fact]
    public async Task Rollback_RestauraTriggerEColunasAnteriores_PreservandoUsuariosEHistorico()
    {
        using var factory = new SqlServerApiFactory();
        await using var db = Contexto(factory);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationAnterior);
        var cargo = await SemearCargosAsync(db);
        var usuario = await InserirLegadoAsync(db, cargo.Id, "rollback@local.dev");
        await migrator.MigrateAsync();

        // Com o schema novo: dado pessoal gravado e auditado pela trigger nova.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={usuario.Id};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value={"198.51.100.2"};
            UPDATE dbo.Usuarios SET Cpf = '11122233344' WHERE Id = {usuario.Id};
            """);

        await migrator.MigrateAsync(MigrationAnterior);

        Assert.DoesNotContain(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
        Assert.Null(await TipoAsync(db, "Usuarios", "Cpf"));
        Assert.Null(await TipoAsync(db, "_usuarios_hist", "Telefone"));
        Assert.Equal("nvarchar(512)", await TipoAsync(db, "Usuarios", "Email"));
        Assert.Equal("0", await EscalarAsync(db, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_Usuarios_Cpf'"));
        Assert.Equal("rollback@local.dev", await EscalarAsync(db, $"SELECT Email FROM dbo.Usuarios WHERE Id = '{usuario.Id}'"));
        Assert.Equal("1", await EscalarAsync(db, $"SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = '{usuario.Id}'"));

        // A trigger restaurada funciona sobre o schema anterior (não referencia colunas removidas).
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={usuario.Id};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value={"198.51.100.3"};
            UPDATE dbo.Usuarios SET Nome = N'Depois do rollback' WHERE Id = {usuario.Id};
            """);
        Assert.Equal("2", await EscalarAsync(db, $"SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = '{usuario.Id}'"));

        // E a migration pode ser reaplicada depois do rollback.
        await migrator.MigrateAsync();
        Assert.Contains(MigrationEmTeste, await db.Database.GetAppliedMigrationsAsync());
    }
}
