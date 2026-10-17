using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public class ApiProcessUsuariosFotoTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    [Fact]
    public async Task Api_Real_UsuariosEFoto_DePontaAPonta_ComIdentidadeDeRuntimeDeMenorPrivilegio()
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

        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/Auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("csrfToken").GetString());
        var login = await client.PostAsJsonAsync("/api/Auth/login", new { email = ApiProcess.AdminEmail, senha = ApiProcess.AdminSenha });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetProperty("accessToken").GetString());

        var lista = JsonNode.Parse(await client.GetStringAsync("/api/Usuarios"))!.AsArray();
        Assert.Contains(lista, u => (string)u!["email"]! == ApiProcess.AdminEmail);
        var cargoDs = (await client.GetFromJsonAsync<JsonElement>("/api/Cargos")).EnumerateArray()
            .Single(c => c.GetProperty("role").GetString() == "DS").GetProperty("id").GetGuid();

        var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);
        object Corpo(string nascimento, object? responsavel = null) => new
        {
            nome = $"E2E {Guid.NewGuid():N}", email = $"e2e-{Guid.NewGuid():N}@local.dev", senha = $"Forte-{Guid.NewGuid():N}", cargoId = cargoDs,
            cpf = TestHelpers.MascararCpf(TestHelpers.NovoCpf()), telefone = "(11) 98765-4321", dataNascimento = nascimento,
            responsavelNome = responsavel is null ? null : "Responsável E2E", responsavelEmail = responsavel is null ? null : "responsavel.e2e@local.dev",
        };
        var menor = hoje.AddYears(-11).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/Usuarios", Corpo(menor))).StatusCode);
        var criadoMenor = await client.PostAsJsonAsync("/api/Usuarios", Corpo(menor, responsavel: true));
        Assert.Equal(HttpStatusCode.Created, criadoMenor.StatusCode);
        var desbravador = JsonNode.Parse(await criadoMenor.Content.ReadAsStringAsync())!;
        var id = Guid.Parse((string)desbravador["id"]!);
        Assert.Equal("11987654321", (string)desbravador["telefone"]!);
        var criadoAdulto = await client.PostAsJsonAsync("/api/Usuarios", Corpo(hoje.AddYears(-35).ToString("yyyy-MM-dd")));
        Assert.Equal(HttpStatusCode.Created, criadoAdulto.StatusCode);
        var adultoId = Guid.Parse((string)JsonNode.Parse(await criadoAdulto.Content.ReadAsStringAsync())!["id"]!);

        var alteracao = await client.PutAsJsonAsync($"/api/Usuarios/{id}", new
        {
            nome = "Desbravador E2E Alterado", email = (string)desbravador["email"]!, cargoId = cargoDs, telefone = "(21) 3456-7890",
        });
        Assert.Equal(HttpStatusCode.OK, alteracao.StatusCode);
        var alterado = JsonNode.Parse(await alteracao.Content.ReadAsStringAsync())!;
        Assert.Equal(("2134567890", "Responsável E2E"), ((string)alterado["telefone"]!, (string)alterado["responsavelNome"]!));

        var rota = $"/api/Usuarios/{id}/foto";
        var envio = await client.PutAsync(rota, FotosTestKit.Multipart(FotosTestKit.ComExif(FotosTestKit.Jpeg(1600, 1200), 6)));
        Assert.True(envio.StatusCode == HttpStatusCode.OK, await envio.Content.ReadAsStringAsync());
        Assert.Equal(rota, (string)JsonNode.Parse(await envio.Content.ReadAsStringAsync())!["fotoUrl"]!);
        var baixada = await client.GetAsync(rota);
        var bytes = await baixada.Content.ReadAsByteArrayAsync();
        Assert.Equal("image/webp", baixada.Content.Headers.ContentType?.MediaType);
        Assert.Equal((768, 1024), FotosTestKit.Dimensoes(bytes));
        Assert.False(FotosTestKit.Contem(bytes, FotosTestKit.MarcadorExif));
        var condicional = new HttpRequestMessage(HttpMethod.Get, rota);
        condicional.Headers.IfNoneMatch.Add(baixada.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(condicional)).StatusCode);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PutAsync(rota, FotosTestKit.Multipart(new byte[11 * 1024 * 1024]))).StatusCode);
        var comCompressao = FotosTestKit.Multipart(FotosTestKit.Jpeg(200, 200));
        comCompressao.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PutAsync(rota, comCompressao)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PutAsync(rota, FotosTestKit.Multipart(FotosTestKit.PngBomba(50_000, 50_000), "bomba.png", "image/png"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(rota, FotosTestKit.Multipart(FotosTestKit.Png(300, 300), "nova.png", "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(rota)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(rota)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(rota)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/Usuarios/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/Usuarios/{adultoId}")).StatusCode);

        Assert.False(Directory.Exists(Path.Combine(api.FotosPath!, "usuarios", id.ToString("N"))));
        await using var verificacao = await SqlIdentityEnvironment.OpenHarnessAsync(env.Database);
        Assert.Equal(4, await SqlIdentityEnvironment.ScalarAsync(verificacao,
            $"SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = '{id}' AND TipoOperacao = 'UPDATE' AND IpResponsavel = '127.0.0.1'"));
        Assert.Equal(1, await SqlIdentityEnvironment.ScalarAsync(verificacao,
            $"SELECT COUNT(*) FROM dbo._usuarios_hist WHERE UsuarioId = '{id}' AND TipoOperacao = 'DELETE' AND FotoCaminho IS NULL AND ResponsavelNome = 'Responsável E2E'"));
        Assert.Equal(0, await SqlIdentityEnvironment.ScalarAsync(verificacao, $"SELECT COUNT(*) FROM dbo.Usuarios WHERE Id = '{id}' AND (Ativo = 1 OR FotoCaminho IS NOT NULL)"));
        NoSecret.Assert(api.Output, env.AdminPassword);
    }
}
