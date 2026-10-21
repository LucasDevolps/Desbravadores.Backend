using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

public sealed class UsuariosRgTipoSanguineoDoencasTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private Task<Guid> CargoIdAsync() =>
        TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());

    private async Task<(HttpResponseMessage Response, string Email)> PostAsync(HttpClient client, Action<JsonObject>? extras = null)
    {
        var email = $"saude-{Guid.NewGuid():N}@local.dev";
        var corpo = new JsonObject
        {
            ["nome"] = "Participante Teste",
            ["email"] = email,
            ["senha"] = "Forte-Senha-9x2k",
            ["cargoId"] = (await CargoIdAsync()).ToString(),
            ["cpf"] = TestHelpers.NovoCpf(),
            ["ehCin"] = true,
        };
        extras?.Invoke(corpo);
        return (await client.PostAsJsonAsync("/api/Usuarios", corpo), email);
    }

    private static async Task<JsonObject> ErrosAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject();
    }

    private Task<bool> ExisteAsync(string email) => TestHelpers.WithDbAsync(factory, db => db.Usuarios.AnyAsync(u => u.Email == email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..-")]
    public async Task Post_SemCpf_Retorna400_CpfObrigatorio_ENaoCria(string? cpf)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var (response, email) = await PostAsync(client, c =>
        {
            if (cpf is null) c.Remove("cpf");
            else c["cpf"] = cpf;
        });

        var erros = await ErrosAsync(response);
        Assert.Equal("cpf é obrigatório.", (string)erros["Cpf"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    [Fact]
    public async Task Post_CpfNull_Retorna400_CpfObrigatorio()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["cpf"] = null);
        Assert.Contains("cpf é obrigatório.", (await ErrosAsync(response))["Cpf"]!.AsArray().Select(m => (string)m!));
        Assert.False(await ExisteAsync(email));
    }

    [Theory]
    [InlineData("529.982.247-26")]
    [InlineData("111.111.111-11")]
    [InlineData("5299822472")]
    [InlineData("abc.def.ghi-jk")]
    public async Task Post_CpfInvalido_Retorna400_ENaoCria(string cpf)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["cpf"] = cpf);
        Assert.StartsWith("cpf inválido", (string)(await ErrosAsync(response))["Cpf"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    [Fact]
    public async Task Post_SemRgTipoSanguineoEDoencas_Cria_ComNull()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, _) = await PostAsync(client, c => { c["rg"] = ""; c["tipoSanguineo"] = "  "; c["doencasAlergias"] = null; });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Null(dto.Rg);
        Assert.Null(dto.TipoSanguineo);
        Assert.Null(dto.DoencasAlergias);
        var salvo = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == dto.Id));
        Assert.Equal((null, null, null), (salvo.Rg, salvo.TipoSanguineo, salvo.DoencasAlergias));
    }

    [Fact]
    public async Task Post_ComOsCampos_PersisteNormalizado_ERetornaNoCadastroENaListagem()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        const string doencas = "Alergia fictícia a amendoim.\r\nAsma leve fictícia; usa bombinha (teste).";

        var (response, _) = await PostAsync(client, c =>
        {
            c["rg"] = "  12.345.678-X ";
            c["tipoSanguineo"] = " ab- ";
            c["doencasAlergias"] = $"  {doencas}\n ";
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("12.345.678-X", (string)json["rg"]!);
        Assert.Equal("AB-", (string)json["tipoSanguineo"]!);
        Assert.Equal(doencas, (string)json["doencasAlergias"]!);

        var id = (Guid)json["id"]!;
        var item = JsonNode.Parse(await client.GetStringAsync("/api/Usuarios"))!.AsArray().Single(u => (Guid)u!["id"]! == id)!;
        Assert.Equal("12.345.678-X", (string)item["rg"]!);
        Assert.Equal("AB-", (string)item["tipoSanguineo"]!);
        Assert.Equal(doencas, (string)item["doencasAlergias"]!);
    }

    [Theory]
    [InlineData("A+")] [InlineData("A-")] [InlineData("B+")] [InlineData("B-")]
    [InlineData("AB+")] [InlineData("AB-")] [InlineData("O+")] [InlineData("o-")]
    public async Task Post_OsOitoTiposSanguineos_SaoAceitos(string tipo)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, _) = await PostAsync(client, c => c["tipoSanguineo"] = tipo);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(tipo.ToUpperInvariant(), (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!.TipoSanguineo);
    }

    [Theory]
    [InlineData("0+")]
    [InlineData("A")]
    [InlineData("AB")]
    [InlineData("A +")]
    [InlineData("C+")]
    [InlineData("A−")]
    [InlineData("O positivo")]
    public async Task Post_TipoSanguineoForaDoConjunto_Retorna400_ENaoCria(string tipo)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["tipoSanguineo"] = tipo);
        Assert.StartsWith("tipoSanguineo inválido", (string)(await ErrosAsync(response))["TipoSanguineo"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    [Theory]
    [InlineData("123456789012345678901")]
    [InlineData("ABC")]
    [InlineData("12.345.678-É")]
    [InlineData("12;345")]
    [InlineData("12\t345")]
    public async Task Post_RgInvalido_Retorna400_ENaoCria(string rg)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["rg"] = rg);
        Assert.True((await ErrosAsync(response)).ContainsKey("Rg"));
        Assert.False(await ExisteAsync(email));
    }

    [Fact]
    public async Task Post_DoencasAlergiasNoLimite_PersisteIntegral_EUmAcima_Retorna400SemTruncar()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var limite = string.Concat(Enumerable.Repeat("Alergia fictícia ção\n", 100))[..1999] + "X";
        Assert.Equal(2000, limite.Length);

        var (aceito, _) = await PostAsync(client, c => c["doencasAlergias"] = limite);
        Assert.Equal(HttpStatusCode.Created, aceito.StatusCode);
        var id = (await aceito.Content.ReadFromJsonAsync<UsuarioListItemDto>())!.Id;
        Assert.Equal(limite, await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Id == id).Select(u => u.DoencasAlergias).SingleAsync()));

        var (recusado, email) = await PostAsync(client, c => c["doencasAlergias"] = limite + "Y");
        Assert.Equal("doencasAlergias deve ter no máximo 2000 caracteres.", (string)(await ErrosAsync(recusado))["DoencasAlergias"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    [Theory]
    [InlineData("Alergia a 🥜")]
    [InlineData("Alergia\tcom tab")]
    [InlineData("Аллергия")]
    public async Task Post_DoencasAlergiasForaDaPagina1252_Retorna400(string texto)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["doencasAlergias"] = texto);
        Assert.StartsWith("doencasAlergias contém caracteres não suportados", (string)(await ErrosAsync(response))["DoencasAlergias"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    [Fact]
    public async Task DadosDeSaude_NaoSaemParaQuemNaoGerenciaCadastros_NemEmMe()
    {
        using var admin = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        const string marcador = "Alergia fictícia PRIVACIDADE";
        var (criado, _) = await PostAsync(admin, c => { c["doencasAlergias"] = marcador; c["tipoSanguineo"] = "B+"; c["rg"] = "MG-12.345.678"; });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);

        var (membro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        using (membro)
        {
            var negado = await membro.GetAsync("/api/Usuarios");
            Assert.Equal(HttpStatusCode.Forbidden, negado.StatusCode);
            Assert.DoesNotContain(marcador, await negado.Content.ReadAsStringAsync());

            var me = JsonNode.Parse(await membro.GetStringAsync("/api/Auth/Me"))!.AsObject();
            Assert.DoesNotContain(me, p => p.Key is "cpf" or "rg" or "tipoSanguineo" or "doencasAlergias");
        }
    }
}
