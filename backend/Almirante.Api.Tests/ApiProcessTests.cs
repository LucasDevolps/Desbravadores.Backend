using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using static Almirante.Api.Tests.SqlIdentityEnvironment;

namespace Almirante.Api.Tests;

// Startup REAL da API (processo dotnet Almirante.Api.dll, como no container). Os testes sem SQL Server provam a
// recusa técnica de "sa" e do segredo dele, e a ausência de fallback; os com SQL Server provam a subida com a
// identidade administrativa dedicada, as operações normais pela identidade de runtime e o restart sem "sa".
public class ApiProcessRefusalTests
{
    private const string NaoExiste = "Server=127.0.0.1,1;Database=almirante;Encrypt=True;TrustServerCertificate=True";

    private static Dictionary<string, string> Config(string? admin, string? saSecretName = null)
    {
        var env = ApiProcess.BaseEnvironment();
        env["ConnectionStrings__almirante"] = NaoExiste;
        env["DbCredentials__AppUser"] = "almirante_user_bd";
        if (admin is not null) env["ConnectionStrings__AlmiranteAdmin"] = admin;
        if (saSecretName is not null) env[saSecretName] = "ValorSecretoDoSa-987";
        return env;
    }

    private static string Admin(string user) =>
        $"Server=127.0.0.1,1;Database=almirante;User Id={user};Password=SenhaAdmin-Nao-Usada-123;Encrypt=True;TrustServerCertificate=True";

    private static async Task<string> RefusedAsync(Dictionary<string, string> env)
    {
        await using var api = ApiProcess.Start(env);
        var (started, exitCode) = await api.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.False(started, "a API não pode subir com esta configuração");
        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("SenhaAdmin-Nao-Usada-123", api.Output);
        Assert.DoesNotContain("ValorSecretoDoSa-987", api.Output);
        return api.Output;
    }

    [Theory]
    [InlineData("sa")]
    [InlineData("SA")]
    public async Task Api_RecusaIniciarComSaComoIdentidadeAdministrativa(string user)
    {
        var saida = await RefusedAsync(Config(Admin(user)));

        Assert.Contains("'sa'", saida);
        Assert.Contains("ConnectionStrings:AlmiranteAdmin", saida);
    }

    [Fact]
    public async Task Api_RecusaIniciarComSaNaConnectionStringDeRuntimeSemDbCredentials()
    {
        var env = ApiProcess.BaseEnvironment();
        env["ConnectionStrings__almirante"] = "Server=127.0.0.1,1;Database=almirante;User Id=sa;Password=SenhaAdmin-Nao-Usada-123;Encrypt=True;TrustServerCertificate=True";

        Assert.Contains("'sa'", await RefusedAsync(env));
    }

    [Theory]
    [InlineData("SQL_SA_PASSWORD")]
    [InlineData("MSSQL_SA_PASSWORD")]
    public async Task Api_RecusaIniciarSeORecebeOSegredoDoSaNoAmbiente(string nome)
    {
        var saida = await RefusedAsync(Config(Admin("almirante_admin_bd"), nome));

        Assert.Contains(nome, saida);
    }

    [Fact]
    public async Task Api_SemIdentidadeAdministrativa_NaoTemFallbackParaSa()
    {
        var saida = await RefusedAsync(Config(admin: null));

        Assert.Contains("fallback para 'sa'", saida);
    }

    [Fact]
    public async Task Api_ComAdministradorIgualAoRuntime_Recusa()
    {
        var saida = await RefusedAsync(Config(Admin("almirante_user_bd")));

        // Só ASCII: o console do processo filho no Windows do CI usa a code page do sistema, não UTF-8.
        Assert.Contains("use credenciais distintas", saida);
    }

    [Theory]
    [InlineData("Warn")]
    [InlineData("Off")]
    public async Task Api_RecusaOModoPermissivoAntigoDeAuditoriaAdministrativa(string modo)
    {
        var env = Config(Admin("almirante_admin_bd"));
        env["Security__AdminPrivilegeCheck"] = modo;

        Assert.Contains("foi removida", await RefusedAsync(env));
    }
}

[Trait("Category", "RequiresSqlServer")]
public class ApiProcessSqlServerTests
{
    private static Dictionary<string, string> Config(SqlIdentityEnvironment env, string? adminCs = null)
    {
        var config = ApiProcess.BaseEnvironment();
        config["ConnectionStrings__almirante"] = env.AppCs;
        config["ConnectionStrings__AlmiranteAdmin"] = adminCs ?? env.AdminCs;
        config["DbCredentials__AppUser"] = env.AppUser;
        return config;
    }

    // Sessões que o SQL Server viu vindas do processo da API: só podem ser das duas identidades da aplicação.
    private static async Task<List<string>> LoginsDoProcessoAsync(int pid)
    {
        await using var harness = await OpenHarnessAsync();
        return await ColumnAsync(harness, $"SELECT DISTINCT LOWER(login_name) FROM sys.dm_exec_sessions WHERE host_process_id = {pid} AND is_user_process = 1");
    }

    [Fact]
    public async Task Api_SobeComAdministradorDedicado_ExecutaOperacoesNormais_EReiniciaSemSa()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();

        for (var partida = 1; partida <= 2; partida++)
        {
            await using var api = ApiProcess.Start(Config(env));
            var (started, exitCode) = await api.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.True(started, $"partida {partida}: a API deveria subir (código {exitCode}). Saída:\n{api.Output}");

            using var client = api.CreateClient(await api.Listening);
            await ApiProcess.ExercitarOperacoesNormaisAsync(client);

            // O SQL Server só viu esta API autenticar como a identidade administrativa (migrations/provisionamento/
            // rotação) e como a de runtime — nunca como "sa" — e nenhum segredo apareceu na saída do processo.
            var logins = await LoginsDoProcessoAsync(api.ProcessId);
            Assert.Contains(env.AppUser.ToLowerInvariant(), logins);
            Assert.All(logins, login => Assert.Contains(login, new[] { env.AppUser.ToLowerInvariant(), env.AdminUser.ToLowerInvariant() }));
            Assert.DoesNotContain("sa", logins);
            NoSecret.Assert(api.Output, env.AdminPassword);
        }
    }

    [Fact]
    public async Task Api_ComAdministradorSysadmin_RecusaIniciar_SemAlterarOBanco()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        var login = $"sysadmin_{env.Suffix}";
        var senha = NewPassword();
        env.TrackLogin(login);
        await using (var harness = await OpenHarnessAsync())
        {
            await ExecAsync(harness, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{senha}', CHECK_POLICY = OFF; ALTER SERVER ROLE [sysadmin] ADD MEMBER [{login}];");
        }
        var adminCs = WithAdmin(new SqlConnectionStringBuilder(env.AppCs), login, senha);

        await using var api = ApiProcess.Start(Config(env, adminCs));
        var (started, exitCode) = await api.WaitAsync(TimeSpan.FromSeconds(120));

        Assert.False(started);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("papel de servidor sysadmin", api.Output);
        NoSecret.Assert(api.Output, senha);
        await using var verificacao = await OpenHarnessAsync(env.Database);
        Assert.Equal(0, await ScalarAsync(verificacao, "SELECT COUNT(*) FROM sys.tables"));
    }
}

// /api/Eventos (issue #56) no binário REAL da API: Kestrel de verdade (RemoteIpAddress real, sem o cabeçalho
// de teste), execution strategy/retry do Aspire, identidade de runtime SEM DELETE e SEM escrita direta em
// historico_eventos. Prova que a exclusão auditada funciona só pela cadeia de propriedade do trigger.
[Trait("Category", "RequiresSqlServer")]
public class ApiProcessEventosTests
{
    [Fact]
    public async Task Api_Real_EventosDePonta_APonta_ComIdentidadeDeRuntimeDeMenorPrivilegio()
    {
        await using var env = new SqlIdentityEnvironment();
        await env.BootstrapAsync();
        var config = ApiProcess.BaseEnvironment();
        config["ConnectionStrings__almirante"] = env.AppCs;
        config["ConnectionStrings__AlmiranteAdmin"] = env.AdminCs;
        config["DbCredentials__AppUser"] = env.AppUser;

        await using var api = ApiProcess.Start(config);
        var (started, exitCode) = await api.WaitAsync(TimeSpan.FromSeconds(120));
        Assert.True(started, $"a API deveria subir (código {exitCode}). Saída:\n{api.Output}");
        using var client = api.CreateClient(await api.Listening);

        async Task CsrfAsync()
        {
            var response = await client.GetAsync("/api/Auth/csrf");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("csrfToken").GetString());
        }
        await CsrfAsync();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new { email = ApiProcess.AdminEmail, senha = ApiProcess.AdminSenha });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // membros criados pela conexão do harness (a API só conhece o seed)
        var membros = new List<Guid>();
        await using (var harness = await SqlIdentityEnvironment.OpenHarnessAsync(env.Database))
        {
            for (var i = 0; i < 3; i++)
            {
                var id = Guid.NewGuid();
                membros.Add(id);
                await SqlIdentityEnvironment.ExecAsync(harness, $"""
                    INSERT INTO dbo.Usuarios (Id, Nome, Email, EmailNormalizado, SenhaHash, CargoId, DataCriacao, SecurityVersion, FalhasLoginConsecutivas)
                    SELECT TOP 1 '{id}', N'Membro real {i}', N'real{i}-{id:N}@local.dev', N'REAL{i}-{id:N}@LOCAL.DEV', N'x', Id, SYSUTCDATETIME(), 0, 0 FROM dbo.Cargos WHERE Role = N'DS'
                    """);
            }
        }

        async Task<HttpResponseMessage> Post(object membrosJson, string chave)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Eventos")
            {
                Content = JsonContent.Create(EventosTestKit.Corpo(membrosJson)),
            };
            request.Headers.Add("Idempotency-Key", chave);
            return await client.SendAsync(request);
        }

        // POST com GUID único e replay com array de um elemento (mesma operação): 201 e depois 200
        var chave = Guid.NewGuid().ToString();
        var criado = await Post(membros[0], chave);
        Assert.Equal(System.Net.HttpStatusCode.Created, criado.StatusCode);
        var evento = await EventosTestKit.LerAsync(criado);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await Post(new[] { membros[0] }, chave)).StatusCode);

        // POST com vários membros (outro passeio) e GET
        var varios = await EventosTestKit.LerAsync(await Post(new[] { membros[1], membros[2] }, Guid.NewGuid().ToString()));
        Assert.Equal((20m, 40m), (varios.ValorPorMembro, varios.Total));
        var lista = await client.GetFromJsonAsync<Almirante.Api.Dtos.EventosResponse>("/api/Eventos");
        Assert.Contains(evento.Id, lista!.Items.Select(i => i.Id));

        // PUT removendo um participante (motivo) e conflito de versão desatualizada
        var put = await client.PutAsJsonAsync($"/api/Eventos/{varios.Id}",
            EventosTestKit.Corpo(membros[1], varios.DataEvento, varios.Local, 12m, versao: varios.Versao, motivo: "removido no teste real"));
        Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);
        var editado = await EventosTestKit.LerAsync(put);
        Assert.Equal((22m, 22m), (editado.ValorPorMembro, editado.Total));
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync($"/api/Eventos/{varios.Id}", EventosTestKit.Corpo(membros[1], versao: varios.Versao))).StatusCode);

        // DELETE: exclusão lógica + histórico gravado pelo trigger com o IP real da conexão
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Eventos/{evento.Id}")
        {
            Content = JsonContent.Create(new { motivo = "exclusao no processo real", versao = evento.Versao }),
        };
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Eventos/{evento.Id}")
        {
            Content = JsonContent.Create(new { motivo = "de novo", versao = evento.Versao }),
        })).StatusCode);

        await using var verificacao = await SqlIdentityEnvironment.OpenHarnessAsync(env.Database);
        Assert.Equal(1, await SqlIdentityEnvironment.ScalarAsync(verificacao, $"SELECT COUNT(*) FROM dbo.historico_eventos WHERE EventoId = '{evento.Id}' AND IpResponsavel = '127.0.0.1' AND Motivo = N'exclusao no processo real'"));
        Assert.Equal(1, await SqlIdentityEnvironment.ScalarAsync(verificacao, $"SELECT COUNT(*) FROM dbo.lancamentos_deletados WHERE EventoId = '{evento.Id}' AND Finalidade IS NULL"));
        Assert.Equal(1, await SqlIdentityEnvironment.ScalarAsync(verificacao, $"SELECT COUNT(*) FROM dbo.lancamentos_deletados WHERE EventoId = '{varios.Id}' AND Motivo = N'removido no teste real'"));

        // a identidade de runtime só enxerga as duas identidades da aplicação e nada de "sa"
        var logins = await SqlIdentityEnvironment.ColumnAsync(verificacao, $"SELECT DISTINCT LOWER(login_name) FROM sys.dm_exec_sessions WHERE host_process_id = {api.ProcessId} AND is_user_process = 1");
        Assert.DoesNotContain("sa", logins);
        NoSecret.Assert(api.Output, env.AdminPassword);
    }
}
