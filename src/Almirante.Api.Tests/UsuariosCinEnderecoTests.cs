using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

public sealed class UsuariosCinEnderecoTests : IClassFixture<AlmiranteApiFactory>
{
    private readonly AlmiranteApiFactory factory;

    public UsuariosCinEnderecoTests(AlmiranteApiFactory factory)
    {
        this.factory = factory;
        factory.ViaCep.Reiniciar();
    }

    private Task<Guid> CargoIdAsync() =>
        TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());

    private async Task<(HttpResponseMessage Response, string Email)> PostAsync(HttpClient client, Action<JsonObject>? extras = null)
    {
        var email = $"cin-{Guid.NewGuid():N}@local.dev";
        var corpo = new JsonObject
        {
            ["nome"] = "Participante Teste",
            ["email"] = email,
            ["senha"] = "Forte-Senha-9x2k",
            ["cargoId"] = (await CargoIdAsync()).ToString(),
            ["cpf"] = TestHelpers.NovoCpf(),
        };
        extras?.Invoke(corpo);
        return (await client.PostAsJsonAsync("/api/Usuarios", corpo), email);
    }

    private static async Task<JsonObject> ErrosAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject();
    }

    private Task<bool> ExisteAsync(string email) => TestHelpers.WithDbAsync(factory, db => db.Usuarios.AnyAsync(u => u.Email == email));

    [Fact]
    public async Task Post_SemOsCamposNovos_GravaEhCinFalse_SemDataESemEndereco_ESemConsultarOViaCep()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, _) = await PostAsync(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.False((bool)json["ehCin"]!);
        Assert.True(json.ContainsKey("dataExpiracao") && json["dataExpiracao"] is null);
        Assert.True(json.ContainsKey("endereco") && json["endereco"] is null);
        var salvo = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == (Guid)json["id"]!));
        Assert.Equal((false, null, null, null), (salvo.EhCin, salvo.DataExpiracao, salvo.EnderecoCep, salvo.EnderecoLocalidade));
        Assert.Empty(factory.ViaCep.Consultas);
    }

    [Fact]
    public async Task Post_ComCinValidadeEEndereco_PersisteTodosOsCampos_ERetornaNoCadastroENaListagem()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, _) = await PostAsync(client, c =>
        {
            c["ehCin"] = true;
            c["dataExpiracao"] = "2020-02-29";
            c["endereco"] = new JsonObject { ["cep"] = " 04382-160 ", ["numero"] = " 123-A ", ["complemento"] = " Apto 12, bloco B " };
        });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.True(dto.EhCin);
        Assert.Equal(new DateOnly(2020, 2, 29), dto.DataExpiracao);
        AssertEnderecoExemplo(dto.Endereco, "04382160", "123-A", "Apto 12, bloco B");
        Assert.Equal(new[] { "04382160" }, factory.ViaCep.Consultas);

        var salvo = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == dto.Id));
        Assert.Equal((true, new DateOnly(2020, 2, 29)), (salvo.EhCin, salvo.DataExpiracao));
        Assert.Equal(
            ("04382160", "Rua Fictícia 04382160", "123-A", "Apto 12, bloco B", "lado ímpar", null, "Jardim de Teste", "São Paulo"),
            (salvo.EnderecoCep, salvo.EnderecoLogradouro, salvo.EnderecoNumero, salvo.EnderecoComplemento, salvo.EnderecoComplementoCep,
                salvo.EnderecoUnidade, salvo.EnderecoBairro, salvo.EnderecoLocalidade));
        Assert.Equal(("SP", "São Paulo", "Sudeste", "3550308", "1004", "11", "7107"),
            (salvo.EnderecoUf, salvo.EnderecoEstado, salvo.EnderecoRegiao, salvo.EnderecoIbge, salvo.EnderecoGia, salvo.EnderecoDdd, salvo.EnderecoSiafi));

        var item = (await client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!.Single(u => u.Id == dto.Id);
        Assert.True(item.EhCin);
        Assert.Equal(new DateOnly(2020, 2, 29), item.DataExpiracao);
        AssertEnderecoExemplo(item.Endereco, "04382160", "123-A", "Apto 12, bloco B");
    }

    internal static void AssertEnderecoExemplo(EnderecoDto? endereco, string cep, string? numero, string? complemento)
    {
        Assert.NotNull(endereco);
        Assert.Equal((cep, $"Rua Fictícia {cep}", numero, complemento, "lado ímpar", null, "Jardim de Teste"),
            (endereco.Cep, endereco.Logradouro, endereco.Numero, endereco.Complemento, endereco.ComplementoCep, endereco.Unidade, endereco.Bairro));
        Assert.Equal(("São Paulo", "SP", "São Paulo", "Sudeste", "3550308", "1004", "11", "7107"),
            (endereco.Localidade, endereco.Uf, endereco.Estado, endereco.Regiao, endereco.Ibge, endereco.Gia, endereco.Ddd, endereco.Siafi));
    }

    [Fact]
    public async Task Post_CepComZerosAEsquerdaECamposVaziosDoViaCep_PreservaZeros_EGravaVaziosComoNull()
    {
        factory.ViaCep.Responder = (cep, _) => Task.FromResult(ViaCepFalso.Json($$"""
            { "cep": "01001-000", "logradouro": "", "complemento": "  ", "unidade": "", "bairro": "", "localidade": "Cidade Teste",
              "uf": "AC", "estado": "Acre", "regiao": "Norte", "ibge": "0012345", "gia": "", "ddd": "09", "siafi": "0001", "novoCampo": 1 }
            """));
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var (response, _) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "01001000" });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var e = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!.Endereco!;
        Assert.Equal(("01001000", null, null, null, null, null, null), (e.Cep, e.Logradouro, e.Numero, e.Complemento, e.ComplementoCep, e.Unidade, e.Bairro));
        Assert.Equal(("Cidade Teste", "AC", "Acre", "Norte", "0012345", null, "09", "0001"),
            (e.Localidade, e.Uf, e.Estado, e.Regiao, e.Ibge, e.Gia, e.Ddd, e.Siafi));
    }

    [Fact]
    public async Task Post_EhCinNull_Retorna400_ENaoCria()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["ehCin"] = null);
        Assert.StartsWith("ehCin deve ser true ou false", (string)(await ErrosAsync(response))["EhCin"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    [Theory]
    [InlineData("4382-160")]
    [InlineData("04382-16")]
    [InlineData("043821600")]
    [InlineData("04382 160")]
    [InlineData("04.382-160")]
    [InlineData("0438-2160")]
    [InlineData("abcde-fgh")]
    [InlineData("０４３８２１６０")]
    public async Task Post_CepMalFormado_Retorna400_SemConsultarOViaCep_ENaoCria(string cep)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = cep, ["numero"] = "1" });
        Assert.StartsWith("endereco.cep inválido", (string)(await ErrosAsync(response))["Endereco.Cep"]![0]!);
        Assert.False(await ExisteAsync(email));
        Assert.Empty(factory.ViaCep.Consultas);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("omitido")]
    public async Task Post_EnderecoSemCep_Retorna400_ENaoCria(string? cep)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c =>
        {
            var endereco = new JsonObject { ["numero"] = "10" };
            if (cep != "omitido") endereco["cep"] = cep;
            c["endereco"] = endereco;
        });
        Assert.Equal("endereco.cep é obrigatório quando endereco é informado.", (string)(await ErrosAsync(response))["Endereco.Cep"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    [Fact]
    public async Task Post_EnderecoNull_CriaSemEndereco()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, _) = await PostAsync(client, c => c["endereco"] = null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!.Endereco);
    }

    [Theory]
    [InlineData("""{ "erro": "true" }""")]
    [InlineData("""{ "erro": true }""")]
    public async Task Post_CepInexistente_Retorna400_ENaoCria(string resposta)
    {
        factory.ViaCep.Responder = (_, _) => Task.FromResult(ViaCepFalso.Json(resposta));
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "99999-999" });
        Assert.StartsWith("endereco.cep não encontrado no ViaCEP", (string)(await ErrosAsync(response))["Endereco.Cep"]![0]!);
        Assert.False(await ExisteAsync(email));
    }

    public static TheoryData<string, string> FalhasDoViaCep => new()
    {
        { "500", "indisponível" },
        { "503", "indisponível" },
        { "429", "indisponível" },
        { "404", "indisponível" },
        { "400", "resposta inválida" },
        { "rede", "indisponível" },
        { "lento", "não respondeu no prazo" },
        { """<html>erro</html>""", "resposta inválida" },
        { """[]""", "resposta inválida" },
        { """{ "erro": "false" }""", "resposta inválida" },
        { """{ "cep": "01001-000", "localidade": "São Paulo", "uf": "SP" }""", "resposta inválida" },
        { """{ "cep": "04382-160", "uf": "SP" }""", "resposta inválida" },
        { """{ "cep": "04382-160", "localidade": "São Paulo", "uf": "sp" }""", "resposta inválida" },
        { """{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SP", "ibge": "35A0308" }""", "resposta inválida" },
        { """{ "cep": "04382-160", "localidade": "São Paulo", "uf": "SP", "ddd": 11 }""", "resposta inválida" },
        { """{ "cep": "04382-160", "localidade": "Москва", "uf": "SP" }""", "resposta inválida" },
        { "logradouro-longo", "resposta inválida" },
        { "resposta-enorme", "resposta inválida" },
    };

    internal static Func<string, CancellationToken, Task<HttpResponseMessage>> Falha(string tipo) => tipo switch
    {
        "rede" => (_, _) => throw new HttpRequestException("conexão recusada (teste)"),
        "lento" => async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return ViaCepFalso.Json("{}"); },
        "logradouro-longo" => (cep, _) =>
        {
            var json = ViaCepFalso.Exemplo(cep);
            json["logradouro"] = new string('R', 201);
            return Task.FromResult(ViaCepFalso.Json(json.ToJsonString()));
        },
        "resposta-enorme" => (cep, _) =>
        {
            var json = ViaCepFalso.Exemplo(cep);
            json["extra"] = new string('x', 20 * 1024);
            return Task.FromResult(ViaCepFalso.Json(json.ToJsonString()));
        },
        _ when int.TryParse(tipo, out var status) => (_, _) => Task.FromResult(ViaCepFalso.Json("{}", (HttpStatusCode)status)),
        _ => (_, _) => Task.FromResult(ViaCepFalso.Json(tipo)),
    };

    [Theory]
    [MemberData(nameof(FalhasDoViaCep))]
    public async Task Post_FalhaDoViaCep_Retorna503_ENaoCria(string falha, string motivo)
    {
        factory.ViaCep.Responder = Falha(falha);
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var (response, email) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "04382-160", ["numero"] = "1" });

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
        var problema = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Consulta de CEP indisponível.", (string)problema["title"]!);
        Assert.Contains(motivo, (string)problema["detail"]!);
        Assert.EndsWith("Nenhum usuário foi cadastrado.", (string)problema["detail"]!);
        Assert.False(await ExisteAsync(email));
    }

    [Fact]
    public async Task Post_FalhaTransitoria_TentaDeNovoUmaVez_EGrava()
    {
        var tentativas = 0;
        factory.ViaCep.Responder = (cep, ct) => Interlocked.Increment(ref tentativas) == 1
            ? Task.FromResult(ViaCepFalso.Json("{}", HttpStatusCode.ServiceUnavailable))
            : ViaCepFalso.Padrao(cep, ct);
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var (response, _) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "04382160" });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "04382160", "04382160" }, factory.ViaCep.Consultas);
    }

    [Fact]
    public async Task Post_ViaCepSempreForaDoAr_FazNoMaximoDuasTentativas()
    {
        factory.ViaCep.Responder = Falha("503");
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, _) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "04382160" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, factory.ViaCep.Consultas.Count);
    }

    [Theory]
    [InlineData("Numero", "123456789012345678901", "endereco.numero deve ter no máximo 20 caracteres.")]
    [InlineData("Numero", "12 🏠", "endereco.numero contém caracteres não suportados")]
    [InlineData("Numero", "12\n3", "endereco.numero contém caracteres não suportados")]
    [InlineData("Complemento", "Bloco Ж", "endereco.complemento contém caracteres não suportados")]
    public async Task Post_NumeroOuComplementoInvalido_Retorna400_SemConsultarOViaCep(string campo, string valor, string mensagem)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var (response, email) = await PostAsync(client, c =>
            c["endereco"] = new JsonObject { ["cep"] = "04382-160", [campo.ToLowerInvariant()] = valor });
        Assert.StartsWith(mensagem, (string)(await ErrosAsync(response))[$"Endereco.{campo}"]![0]!);
        Assert.False(await ExisteAsync(email));
        Assert.Empty(factory.ViaCep.Consultas);
    }

    [Fact]
    public async Task Post_ComplementoNoLimite_PersisteIntegral_EUmAcima_Retorna400()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var limite = string.Concat(Enumerable.Repeat("Apto ção ", 20))[..99] + "Z";
        var (aceito, _) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "04382160", ["complemento"] = limite });
        Assert.Equal(HttpStatusCode.Created, aceito.StatusCode);
        Assert.Equal(limite, (await aceito.Content.ReadFromJsonAsync<UsuarioListItemDto>())!.Endereco!.Complemento);

        var (recusado, _) = await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "04382160", ["complemento"] = limite + "Y" });
        Assert.Equal("endereco.complemento deve ter no máximo 100 caracteres.", (string)(await ErrosAsync(recusado))["Endereco.Complemento"]![0]!);
    }

    [Fact]
    public async Task CepDoCadastro_NaoVaiParaOLog_NemNaUrlDoHttpClient_NemNaFalha()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/Cep/13579-246")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "13579-246" })).Response.StatusCode);
        factory.ViaCep.Responder = Falha("503");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await PostAsync(client, c => c["endereco"] = new JsonObject { ["cep"] = "13579246" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/Cep/13579246")).StatusCode);

        Assert.All(factory.ViaCep.Consultas, c => Assert.Equal("13579246", c));
        Assert.Contains(factory.Logs.Entries, e => e.Contains("recusada com HTTP 503", StringComparison.Ordinal));
        // Só a rota da própria consulta GET /api/Cep/{cep} (CEP pesquisado, sem vínculo com usuário) aparece no log de recusas.
        var vazados = factory.Logs.Entries
            .Where(e => e.Contains("13579246", StringComparison.Ordinal) || e.Contains("13579-246", StringComparison.Ordinal))
            .Where(e => !e.Contains("Rota=/api/Cep/13579246", StringComparison.Ordinal))
            .ToList();
        Assert.True(vazados.Count == 0, string.Join(" || ", vazados.Select(v => v[..Math.Min(400, v.Length)])));
    }

    [Fact]
    public async Task Endereco_NaoSaiEmMe()
    {
        var (membro, id) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        using (membro)
        {
            var me = JsonNode.Parse(await membro.GetStringAsync("/api/Auth/Me"))!.AsObject();
            Assert.Equal(id, (Guid)me["id"]!);
            Assert.DoesNotContain(me, p => p.Key is "endereco" or "ehCin" or "dataExpiracao");
        }
    }

    [Theory]
    [InlineData("04382-160")]
    [InlineData("04382160")]
    public async Task GetCep_DevolveTodosOsCamposDoViaCep(string cep)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var response = await client.GetAsync($"/api/Cep/{cep}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(
            ["cep", "logradouro", "complemento", "unidade", "bairro", "localidade", "uf", "estado", "regiao", "ibge", "gia", "ddd", "siafi"],
            json.Select(p => p.Key));
        Assert.Equal(("04382160", "Rua Fictícia 04382160", "lado ímpar", null, "Jardim de Teste"),
            ((string)json["cep"]!, (string)json["logradouro"]!, (string)json["complemento"]!, (string?)json["unidade"], (string)json["bairro"]!));
        Assert.Equal(("São Paulo", "SP", "São Paulo", "Sudeste", "3550308", "1004", "11", "7107"),
            ((string)json["localidade"]!, (string)json["uf"]!, (string)json["estado"]!, (string)json["regiao"]!, (string)json["ibge"]!,
                (string)json["gia"]!, (string)json["ddd"]!, (string)json["siafi"]!));
        Assert.Equal(new[] { "04382160" }, factory.ViaCep.Consultas);
    }

    [Fact]
    public async Task GetCep_MalFormado_400_Inexistente_404_ViaCepFora_503()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var malFormado = await client.GetAsync("/api/Cep/4382-160");
        Assert.StartsWith("cep inválido", (string)(await ErrosAsync(malFormado))["Cep"]![0]!);
        Assert.Empty(factory.ViaCep.Consultas);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/Cep/{ViaCepFalso.CepInexistente}")).StatusCode);

        factory.ViaCep.Responder = Falha("lento");
        var lento = await client.GetAsync("/api/Cep/04382160");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lento.StatusCode);
        Assert.Contains("não respondeu no prazo", (string)JsonNode.Parse(await lento.Content.ReadAsStringAsync())!["detail"]!);
    }

    [Fact]
    public async Task GetCep_ExigeGestaoDeCadastros()
    {
        using var anonimo = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/Cep/04382160")).StatusCode);

        var (membro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        using (membro)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await membro.GetAsync("/api/Cep/04382160")).StatusCode);
        }

        Assert.Empty(factory.ViaCep.Consultas);
    }
}
