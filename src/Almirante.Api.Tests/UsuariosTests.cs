using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// /api/Usuarios sem SQL Server: listagem, validação de entrada e regras que não dependem de trigger/transação.
// Escrita auditada (PUT/DELETE), e-mail único no banco e histórico: ver UsuariosSqlServerTests.
public sealed class UsuariosTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private async Task<(Guid Ativo, Guid Inativo)> SemearAtivoEInativoAsync()
    {
        var ativo = await TestHelpers.AddUsuarioAsync(factory, $"Ativo {Guid.NewGuid():N}", "DIR");
        var inativo = await TestHelpers.AddUsuarioAsync(factory, $"Inativo {Guid.NewGuid():N}", "DS");
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            var usuario = await db.Usuarios.SingleAsync(u => u.Id == inativo.Id);
            usuario.Ativo = false;
            return await db.SaveChangesAsync();
        });
        return (ativo.Id, inativo.Id);
    }

    private Task<Guid> CargoIdAsync(string role) =>
        TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == role).Select(c => c.Id).SingleAsync());

    [Theory]
    [InlineData("/api/Usuarios")]
    [InlineData("/api/Usuarios?includeInactive=false")]
    public async Task Get_PorPadrao_ListaSomenteUsuariosAtivos(string url)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (ativo, inativo) = await SemearAtivoEInativoAsync();

        var usuarios = (await client.GetFromJsonAsync<List<UsuarioListItemDto>>(url))!;

        Assert.Contains(usuarios, u => u.Id == ativo);
        Assert.DoesNotContain(usuarios, u => u.Id == inativo);
        Assert.All(usuarios, u => Assert.True(u.Ativo));
    }

    [Fact]
    public async Task Get_ComIncludeInactiveTrue_ListaAtivosEInativos()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (ativo, inativo) = await SemearAtivoEInativoAsync();

        var usuarios = (await client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios?includeInactive=true"))!;

        Assert.True(usuarios.Single(u => u.Id == ativo).Ativo);
        Assert.False(usuarios.Single(u => u.Id == inativo).Ativo);
    }

    [Fact]
    public async Task Get_ApresentaONomeDaFuncao_ENaoSomenteOCodigoTecnico()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var diretor = await TestHelpers.AddUsuarioAsync(factory, $"Diretor {Guid.NewGuid():N}", "DIR");

        var json = JsonNode.Parse(await client.GetStringAsync("/api/Usuarios"))!.AsArray();
        var item = json.Single(u => (string)u!["id"]! == diretor.Id.ToString())!;

        Assert.Equal("Diretor", (string)item["funcao"]!);
        // Compatibilidade: o objeto cargo continua presente para os consumidores atuais.
        Assert.Equal("Diretor", (string)item["cargo"]!["nome"]!);
        Assert.Equal("DIR", (string)item["cargo"]!["role"]!);
    }

    [Fact]
    public async Task Get_IncludeInactiveInvalido_Retorna400()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/Usuarios?includeInactive=talvez")).StatusCode);
    }

    public static TheoryData<string, string> CorposInvalidos => new()
    {
        { """{"email":"a@local.dev","senha":"Forte-Senha-9x2k","cargoId":"CARGO"}""", "Nome" },
        { """{"nome":"   ","email":"a@local.dev","senha":"Forte-Senha-9x2k","cargoId":"CARGO"}""", "Nome" },
        { """{"nome":"Fulano","senha":"Forte-Senha-9x2k","cargoId":"CARGO"}""", "Email" },
        { """{"nome":"Fulano","email":"sem-arroba","senha":"Forte-Senha-9x2k","cargoId":"CARGO"}""", "Email" },
        { """{"nome":"Fulano","email":"Fulano <f@local.dev>","senha":"Forte-Senha-9x2k","cargoId":"CARGO"}""", "Email" },
        { """{"nome":"Fulano","email":"f@localhost","senha":"Forte-Senha-9x2k","cargoId":"CARGO"}""", "Email" },
        { """{"nome":"Fulano","email":"f@local.dev","senha":"Forte-Senha-9x2k"}""", "CargoId" },
        { """{"nome":"Fulano","email":"f@local.dev","senha":"Forte-Senha-9x2k","cargoId":"00000000-0000-0000-0000-000000000000"}""", "CargoId" },
        { """{"nome":"Fulano","email":"f@local.dev","cargoId":"CARGO"}""", "Senha" },
        { """{"nome":"Fulano","email":"f@local.dev","senha":"curta","cargoId":"CARGO"}""", "Senha" },
    };

    [Theory]
    [MemberData(nameof(CorposInvalidos))]
    public async Task Post_DadosInvalidos_Retorna400NoCampo_ENaoCria(string corpo, string campo)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var antes = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync());
        var json = corpo.Replace("CARGO", (await CargoIdAsync("DS")).ToString());

        var response = await client.PostAsync("/api/Usuarios", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var erros = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject();
        Assert.Contains(campo, erros.Select(e => e.Key));
        Assert.Equal(antes, await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync()));
    }

    [Fact]
    public async Task Post_CargoInexistenteOuInativo_Retorna400()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var corpo = new { nome = "Fulano", email = $"f-{Guid.NewGuid():N}@local.dev", senha = "Forte-Senha-9x2k", cargoId = Guid.NewGuid() };

        var response = await client.PostAsJsonAsync("/api/Usuarios", corpo);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CargoId", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject().Select(e => e.Key));
    }

    [Fact]
    public async Task Post_AdministradorSoPodeSerCriadoPorAdministrador()
    {
        var (tesoureiro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var corpo = new { nome = "Novo Admin", email = $"adm-{Guid.NewGuid():N}@local.dev", senha = "Forte-Senha-9x2k", cargoId = await CargoIdAsync("ADM") };

        var negado = await tesoureiro.PostAsJsonAsync("/api/Usuarios", corpo);
        Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AnyAsync(u => u.Email == corpo.email)));

        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/Usuarios", corpo)).StatusCode);
    }

    [Fact]
    public async Task Post_CampoAtivoOuAuditoriaNoCorpo_EhIgnorado()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var email = $"ign-{Guid.NewGuid():N}@local.dev";

        var response = await client.PostAsJsonAsync("/api/Usuarios", new
        {
            nome = "Ignora", email, senha = "Forte-Senha-9x2k", cargoId = await CargoIdAsync("DS"),
            ativo = false, solicitanteEhAdmin = true, usuarioResponsavelId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!.Ativo);
    }
}
