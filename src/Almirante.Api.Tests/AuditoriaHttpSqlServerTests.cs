using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Almirante.Api.Tests.AuditoriaHttpTestKit;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

/// <summary>
/// Auditoria HTTP (issue #158) contra SQL Server real: persistência e nulabilidade, independência do rollback do negócio, imutabilidade,
/// nova tentativa sem duplicar, registro recusado isolado e permissões da identidade de runtime.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AuditoriaHttpSqlServerTests : IAsyncLifetime
{
    private const string RollbackPath = "/__tests/auditoria/rollback";
    private const string DescricaoAlterada = "alterada-antes-do-rollback";

    private SqlServerApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqlServerApiFactory(new Dictionary<string, string?> { ["AuditoriaHttp:IntervaloGravacaoMs"] = "60000" },
            configureServices: s => s.AddSingleton<IStartupFilter, FalhaDepoisDeGravar>());
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<T> SqlAsync<T>(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(_factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task FluxoComLogin_SucessoEErros_PersisteComNulabilidadeCoerente_ESemSegredosNoBanco()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var cargoId = await TestHelpers.WithDbAsync(_factory, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());
        var cpf = TestHelpers.NovoCpf();
        var email = $"sql-{Guid.NewGuid():N}@exemplo.com";
        var senha = $"Senha-Forte-{Guid.NewGuid():N}";

        using var lista = await client.GetAsync("/api/Usuarios?ordem=1");
        using var cadastro = await client.PostAsJsonAsync("/api/Usuarios?ordem=2", new JsonObject
        {
            ["nome"] = "Cadastro Auditado", ["email"] = email, ["senha"] = senha, ["cargoId"] = cargoId.ToString(), ["cpf"] = cpf,
            ["rg"] = TestHelpers.RgFicticio, ["doencasAlergias"] = "asma-auditada",
        });
        using var invalido = await client.PutAsJsonAsync($"/api/Lancamentos/{Guid.NewGuid()}?ordem=3", new { valor = -1 });
        using var excecao = await client.GetAsync(AlmiranteApiFactory.ThrowPath + "?ordem=4");
        using var logout = await client.PostAsync("/api/Auth/logout?ordem=5", null);

        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        Assert.True(cadastro.IsSuccessStatusCode, await cadastro.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, excecao.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var registros = await RegistrosAsync(_factory, r => r.Url.Contains("ordem="), minimo: 5);
        var porOrdem = registros.ToDictionary(r => r.Url[^1]);
        Assert.Equal([200, (int)cadastro.StatusCode, 400, 500, 204], "12345".Select(o => porOrdem[o].StatusCode!.Value));
        Assert.All("125".Select(o => porOrdem[o]), r => Assert.Null(r.Exception));
        // 400 de validação também é exceção tratada (ValidationExceptionHandler): tipo registrado, mensagens não.
        Assert.Equal(typeof(FluentValidation.ValidationException).FullName, JsonDocument.Parse(porOrdem['3'].Exception!).RootElement.GetProperty("tipo").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, JsonDocument.Parse(porOrdem['4'].Exception!).RootElement.GetProperty("tipo").GetString());
        Assert.Null(porOrdem['5'].Response);
        Assert.Equal(EstadoCapturaHttp.Completa, porOrdem['2'].EstadoCapturaRequest);

        // Conferência no próprio banco, sem trazer valores sensíveis para o teste: nenhuma linha contém os segredos usados.
        var colunas = "CONCAT([Url], [MetodoHttp], [Request], [Response], [Headers], [Exception], [IpUsuario], [IdentificacaoUsuario], [TraceId])";
        foreach (var segredo in new[] { cpf, email, senha, token, "asma-auditada", AlmiranteApiFactory.AdminSenha, AlmiranteApiFactory.AdminEmail, "falha simulada" })
        {
            Assert.Equal(0, await SqlAsync<int>($"SELECT COUNT(*) FROM auditoria_http WHERE {colunas} LIKE '%' + @s + '%'", ("@s", segredo)));
        }

        Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM auditoria_http WHERE LOWER([Url]) LIKE '%/api/auth/login%'"));
        Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM auditoria_http WHERE ISJSON([Request]) = 0 OR ISJSON([Headers]) = 0"));
    }

    [Fact]
    public async Task RollbackDoNegocio_NaoDesfazNemContaminaOLog()
    {
        using var client = _factory.CreateClient();
        var descricaoAntes = await SqlAsync<string>("SELECT Descricao FROM Cargos WHERE Role = 'DS'");

        using var response = await client.GetAsync(RollbackPath + "?ordem=9");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(descricaoAntes, await SqlAsync<string>("SELECT Descricao FROM Cargos WHERE Role = 'DS'"));
        var registro = Assert.Single(await RegistrosAsync(_factory, r => r.Url.EndsWith("?ordem=9")));
        Assert.Equal(500, registro.StatusCode);
        Assert.Equal(typeof(InvalidOperationException).FullName, JsonDocument.Parse(registro.Exception!).RootElement.GetProperty("tipo").GetString());
        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM auditoria_http WHERE Id = @id", ("@id", registro.Id)));
        AssertSemSegredos([registro], DescricaoAlterada);
    }

    [Fact]
    public async Task Tabela_SoAceitaInsercao_TriggerRecusaUpdateEDelete()
    {
        using var client = _factory.CreateClient();
        using var _ = await client.GetAsync("/api/Auth/csrf?ordem=1");
        var registro = Assert.Single(await RegistrosAsync(_factory, r => r.Url.EndsWith("csrf?ordem=1")));

        foreach (var sql in new[] { "UPDATE auditoria_http SET StatusCode = 299 WHERE Id = @id", "DELETE FROM auditoria_http WHERE Id = @id" })
        {
            var erro = await Assert.ThrowsAsync<SqlException>(() => SqlAsync<object>(sql, ("@id", registro.Id)));
            Assert.Equal(50158, erro.Number);
        }

        Assert.Equal(200, await SqlAsync<int>("SELECT StatusCode FROM auditoria_http WHERE Id = @id", ("@id", registro.Id)));
    }

    [Fact]
    public async Task NovaTentativa_NaoDuplica_ERegistroRecusadoPeloBanco_VaiParaRejeitadosSemTravarOsDemais()
    {
        var repositorio = _factory.Services.GetRequiredService<IRepositorioAuditoriaHttp>();
        var diario = _factory.Services.GetRequiredService<DiarioAuditoriaHttp>();
        var gravador = _factory.Services.GetRequiredService<GravadorAuditoriaHttp>();
        var valido = Registro();
        var outroValido = Registro();
        var recusado = Registro();
        recusado.Request = "isto não é JSON";

        await repositorio.InserirAsync([valido], CancellationToken.None);
        await repositorio.InserirAsync([valido, valido], CancellationToken.None);
        await diario.RegistrarAsync(valido);
        await diario.RegistrarAsync(recusado);
        await diario.RegistrarAsync(outroValido);
        Assert.True(await gravador.GravarPendentesAsync(CancellationToken.None));

        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM auditoria_http WHERE Id = @id", ("@id", valido.Id)));
        Assert.Equal(1, await SqlAsync<int>("SELECT COUNT(*) FROM auditoria_http WHERE Id = @id", ("@id", outroValido.Id)));
        Assert.Equal(0, await SqlAsync<int>("SELECT COUNT(*) FROM auditoria_http WHERE Id = @id", ("@id", recusado.Id)));
        var rejeitados = Directory.GetFiles(Path.Combine(diario.Diretorio, DiarioAuditoriaHttp.PastaRejeitados));
        Assert.Contains(recusado.Id.ToString(), await File.ReadAllTextAsync(Assert.Single(rejeitados)));
    }

    [Fact]
    public async Task EnvioEDownloadDeFoto_ArquivoNuncaGravado_SoTipoETamanho()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
        var alvo = await TestHelpers.AddUsuarioAsync(_factory, "Foto Auditada SQL", "DS");

        using var envio = await client.PutAsync($"/api/Usuarios/{alvo.Id}/foto", FotosTestKit.Multipart(FotosTestKit.Jpeg(160, 160)));
        using var download = await client.GetAsync($"/api/Usuarios/{alvo.Id}/foto");
        var bytes = await download.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, envio.StatusCode);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var registroEnvio = await RegistroAsync(_factory, "PUT", $"/api/Usuarios/{alvo.Id}/foto");
        Assert.Equal((EstadoCapturaHttp.OmitidaPorTipo, EstadoCapturaHttp.Completa), (registroEnvio.EstadoCapturaRequest, registroEnvio.EstadoCapturaResponse));
        var registroDownload = await RegistroAsync(_factory, "GET", $"/api/Usuarios/{alvo.Id}/foto");
        Assert.Equal(EstadoCapturaHttp.OmitidaPorTipo, registroDownload.EstadoCapturaResponse);
        Assert.Equal(bytes.Length, JsonDocument.Parse(registroDownload.Response!).RootElement.GetProperty("tamanhoBytes").GetInt32());
    }

    [Fact]
    public async Task IdentidadeDeRuntime_SoLeEInsere_EConcessaoAMaisEDetectadaERevogada()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        await env.NewManager().InitializeAsync();

        await using (var app = await env.OpenAppAsync())
        {
            Assert.Equal(1, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME('dbo.auditoria_http', 'OBJECT', 'INSERT')"));
            Assert.Equal(1, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME('dbo.auditoria_http', 'OBJECT', 'SELECT')"));
            Assert.Equal(0, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME('dbo.auditoria_http', 'OBJECT', 'UPDATE')"));
            Assert.Equal(0, await ScalarAsync(app, "SELECT HAS_PERMS_BY_NAME('dbo.auditoria_http', 'OBJECT', 'DELETE')"));
            Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(app));
            foreach (var sql in new[] { "UPDATE auditoria_http SET StatusCode = 1", "DELETE FROM auditoria_http" })
            {
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => ExecAsync(app, sql))).Number);
            }
        }

        await using (var harness = await OpenHarnessAsync(env.Database))
        {
            await ExecAsync(harness, $"REVOKE UPDATE ON OBJECT::dbo.auditoria_http FROM [{DbCredentialManager.AppRoleName}]; GRANT UPDATE ON OBJECT::dbo.auditoria_http TO [{env.AppUser}];");
        }

        await using (var comprometido = await env.OpenAppAsync())
        {
            Assert.Contains("UPDATE na tabela só de inserção auditoria_http", await DbPrivilegeAuditor.FindExcessPrivilegesAsync(comprometido));
        }

        await env.BootstrapAsync(env.AdminPassword);
        await env.NewManager().InitializeAsync();
        await using var corrigido = await env.OpenAppAsync();
        Assert.Empty(await DbPrivilegeAuditor.FindExcessPrivilegesAsync(corrigido));
    }

    private static AuditoriaHttp Registro() => new()
    {
        Id = Guid.CreateVersion7(),
        Url = "https://localhost/api/Lancamentos",
        MetodoHttp = "GET",
        Request = SanitizadorAuditoriaHttp.SemCorpo,
        Headers = "{}",
        DataRequestUtc = DateTimeOffset.UtcNow,
        IpUsuario = "127.0.0.1",
        IdentificacaoUsuario = AuditoriaHttp.Anonimo,
        TraceId = "teste",
        StatusCode = 200,
        EstadoCapturaRequest = EstadoCapturaHttp.SemCorpo,
        EstadoCapturaResponse = EstadoCapturaHttp.SemCorpo,
    };

    /// <summary>Endpoint de teste: grava no banco dentro de uma transação e falha antes do commit.</summary>
    private sealed class FalhaDepoisDeGravar : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, proximo) =>
            {
                if (context.Request.Path != RollbackPath)
                {
                    await proximo(context);
                    return;
                }

                var db = context.RequestServices.GetRequiredService<AlmiranteDbContext>();
                await using var transacao = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync($"UPDATE Cargos SET Descricao = '{DescricaoAlterada}' WHERE Role = 'DS'");
                throw new InvalidOperationException("falha depois de gravar, antes do commit");
            });
        };
    }
}
