using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

/// <summary>Contrato e recusas do autoatendimento (/api/Me) e da lista mínima de membros, sem depender do SQL Server.</summary>
public sealed class AutoatendimentoTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private const string MarcadorSaude = "Alergia fictícia AUTOATENDIMENTO";

    private async Task<(HttpClient Client, Guid Id)> DesbravadorComDadosAsync()
    {
        var (client, id) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            var u = await db.Usuarios.SingleAsync(x => x.Id == id);
            u.Cpf = TestHelpers.NovoCpf();
            u.Telefone = "11987654321";
            u.DoencasAlergias = MarcadorSaude;
            u.TipoSanguineo = "O+";
            u.EnderecoCep = "04382160";
            u.EnderecoLocalidade = "São Paulo";
            u.EnderecoUf = "SP";
            u.EnderecoNumero = "10";
            return await db.SaveChangesAsync();
        });
        return (client, id);
    }

    private Task<Usuario> UsuarioAsync(Guid id) => TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == id));

    [Fact]
    public async Task GetMe_DevolveSoOsPropriosDados_ComCargoResumidoEFotoPropria()
    {
        var (client, id) = await DesbravadorComDadosAsync();
        var (outro, outroId) = await DesbravadorComDadosAsync();
        var salvo = await UsuarioAsync(id);

        var json = JsonNode.Parse(await client.GetStringAsync("/api/Me"))!.AsObject();

        Assert.Equal(
            ["id", "nome", "email", "cpf", "dataNascimento", "telefone", "responsavelNome", "responsavelEmail", "responsavelCpf", "responsavelTelefone",
             "rg", "tipoSanguineo", "doencasAlergias", "ehCin", "dataExpiracao", "endereco", "fotoUrl", "cargo"],
            json.Select(p => p.Key));
        Assert.Equal((id, salvo.Email, salvo.Cpf, "11987654321", MarcadorSaude, "O+", "04382160", "10"),
            ((Guid)json["id"]!, (string)json["email"]!, (string?)json["cpf"], (string)json["telefone"]!, (string)json["doencasAlergias"]!,
             (string)json["tipoSanguineo"]!, (string)json["endereco"]!["cep"]!, (string)json["endereco"]!["numero"]!));
        Assert.Null(json["fotoUrl"]);
        Assert.Equal(["id", "nome", "role"], json["cargo"]!.AsObject().Select(p => p.Key));
        Assert.Equal("DS", (string)json["cargo"]!["role"]!);
        Assert.True((await client.GetAsync("/api/Me")).Headers.CacheControl!.NoStore);

        var doOutro = JsonNode.Parse(await outro.GetStringAsync("/api/Me"))!;
        Assert.Equal(outroId, (Guid)doOutro["id"]!);
        Assert.DoesNotContain(salvo.Cpf!, doOutro.ToJsonString());
    }

    [Fact]
    public async Task GetMe_IgnoraQualquerIdentificadorDoCliente()
    {
        var (client, id) = await DesbravadorComDadosAsync();
        var (_, outroId) = await DesbravadorComDadosAsync();

        var porQuery = JsonNode.Parse(await client.GetStringAsync($"/api/Me?id={outroId}&usuarioId={outroId}"))!;

        Assert.Equal(id, (Guid)porQuery["id"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/Me/{outroId}")).StatusCode);
    }

    public static TheoryData<string, object?> PropriedadesAdministrativas => new()
    {
        { "nome", "Outro Nome" }, { "email", "outro@local.dev" }, { "cargoId", Guid.NewGuid() }, { "senha", "Nova-Senha-Forte-9x" },
        { "ativo", false }, { "id", Guid.NewGuid() }, { "role", "ADM" }, { "cargo", "ADM" },
        { "securityVersion", 0 }, { "fotoCaminho", "x" }, { "solicitanteRole", "ADM" }, { "dataCriacao", "2020-01-01T00:00:00Z" },
        { "funcao", "Diretor" }, { "possuiFoto", true }, { "loginBloqueadoAteUtc", null },
    };

    [Theory]
    [MemberData(nameof(PropriedadesAdministrativas))]
    public async Task PutMe_PropriedadeAdministrativaNoCorpo_400_ENadaMuda(string propriedade, object? valor)
    {
        var (client, id) = await DesbravadorComDadosAsync();
        var antes = await UsuarioAsync(id);
        var corpo = new JsonObject { ["telefone"] = "(21) 3456-7890", [propriedade] = JsonValue.Create(valor) };

        var resposta = await client.PutAsJsonAsync("/api/Me", corpo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var erros = JsonNode.Parse(await resposta.Content.ReadAsStringAsync())!["errors"]!.AsObject();
        Assert.True(erros.Select(e => e.Key).Order().SequenceEqual(new[] { $"$.{propriedade}", "request" }.Order()), erros.ToJsonString());
        Assert.Equal("Campo não aceito nesta operação.", (string)erros[$"$.{propriedade}"]!.AsArray().Single()!);
        var depois = await UsuarioAsync(id);
        Assert.Equal((antes.Nome, antes.Email, antes.CargoId, antes.Ativo, antes.SenhaHash, antes.Telefone, antes.SecurityVersion),
            (depois.Nome, depois.Email, depois.CargoId, depois.Ativo, depois.SenhaHash, depois.Telefone, depois.SecurityVersion));
    }

    [Theory]
    [InlineData("cpf", "\"123.456.789-00\"", "Cpf")]
    [InlineData("telefone", "\"123\"", "Telefone")]
    [InlineData("tipoSanguineo", "\"Z+\"", "TipoSanguineo")]
    [InlineData("rg", "\"!!!\"", "Rg")]
    [InlineData("ehCin", "null", "EhCin")]
    [InlineData("dataNascimento", "\"2999-01-01\"", "DataNascimento")]
    [InlineData("responsavelEmail", "\"sem-arroba\"", "ResponsavelEmail")]
    [InlineData("responsavelCpf", "\"111.111.111-11\"", "ResponsavelCpf")]
    [InlineData("endereco", "{\"cep\":\"123\"}", "Endereco.Cep")]
    [InlineData("endereco", "{\"cep\":\"04382160\",\"numero\":\"123456789012345678901\"}", "Endereco.Numero")]
    [InlineData("doencasAlergias", "\"🙂\"", "DoencasAlergias")]
    public async Task PutMe_ReutilizaAsValidacoesDosDadosPessoais(string campo, string valorJson, string chaveErro)
    {
        var (client, id) = await DesbravadorComDadosAsync();
        var antes = await UsuarioAsync(id);

        var resposta = await client.PutAsync("/api/Me", new StringContent($"{{\"{campo}\":{valorJson}}}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var erros = JsonNode.Parse(await resposta.Content.ReadAsStringAsync())!["errors"]!.AsObject();
        Assert.Contains(chaveErro, erros.Select(e => e.Key));
        var depois = await UsuarioAsync(id);
        Assert.Equal((antes.Cpf, antes.Telefone, antes.TipoSanguineo, antes.DoencasAlergias, antes.EnderecoCep),
            (depois.Cpf, depois.Telefone, depois.TipoSanguineo, depois.DoencasAlergias, depois.EnderecoCep));
        Assert.DoesNotContain("123", factory.ViaCep.Consultas);
    }

    [Fact]
    public async Task Membros_ListaSoIdNomeECargoDosAtivos_EmOrdemDeNome()
    {
        var (tesoureiro, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var (_, comDadosId) = await DesbravadorComDadosAsync();
        var sensivel = await UsuarioAsync(comDadosId);
        var inativo = await TestHelpers.AddUsuarioAsync(factory, "AAA Inativo", "DS");
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            (await db.Usuarios.SingleAsync(u => u.Id == inativo.Id)).Ativo = false;
            return await db.SaveChangesAsync();
        });

        var resposta = await tesoureiro.GetAsync("/api/Membros");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        var itens = JsonNode.Parse(texto)!.AsArray();
        Assert.All(itens, i => Assert.Equal(["id", "nome", "cargo", "role"], i!.AsObject().Select(p => p.Key)));
        var esperado = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.Where(u => u.Ativo).OrderBy(u => u.Nome).ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.Nome, Cargo = u.Cargo!.Nome, u.Cargo.Role }).ToListAsync());
        Assert.Equal(esperado.Select(e => (e.Id, e.Nome, e.Cargo, e.Role)),
            itens.Select(i => ((Guid)i!["id"]!, (string)i["nome"]!, (string)i["cargo"]!, (string)i["role"]!)));
        Assert.DoesNotContain(itens, i => (Guid)i!["id"]! == inativo.Id);
        Assert.Contains(itens, i => (Guid)i!["id"]! == comDadosId && (string)i["cargo"]! == "Desbravador");
        foreach (var dado in new[] { sensivel.Cpf!, sensivel.Telefone!, MarcadorSaude, sensivel.Email, "04382160", "O+" })
        {
            Assert.DoesNotContain(dado, texto);
        }
    }
}
