using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

public sealed class UsuariosResponsavelTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static DateOnly HojeCivil() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);

    private static string Data(DateOnly data) => data.ToString("yyyy-MM-dd");

    private Task<Guid> CargoIdAsync() =>
        TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, Dictionary<string, object?> extras)
    {
        var corpo = new Dictionary<string, object?>
        {
            ["nome"] = "Desbravador Teste",
            ["email"] = $"ds-{Guid.NewGuid():N}@local.dev",
            ["senha"] = "Forte-Senha-9x2k",
            ["cargoId"] = await CargoIdAsync(),
            ["cpf"] = TestHelpers.NovoCpf(),
        };
        foreach (var (campo, valor) in extras) corpo[campo] = valor;
        return await client.PostAsJsonAsync("/api/Usuarios", corpo);
    }

    private static async Task<JsonObject> ErrosAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject();
    }

    private static Dictionary<string, object?> Menor(params (string Campo, object? Valor)[] extras)
    {
        var corpo = new Dictionary<string, object?> { ["dataNascimento"] = Data(HojeCivil().AddYears(-12)) };
        foreach (var (campo, valor) in extras) corpo[campo] = valor;
        return corpo;
    }

    private static Dictionary<string, object?> Maior(params (string Campo, object? Valor)[] extras)
    {
        var corpo = new Dictionary<string, object?> { ["dataNascimento"] = Data(HojeCivil().AddYears(-30)) };
        foreach (var (campo, valor) in extras) corpo[campo] = valor;
        return corpo;
    }

    [Fact]
    public async Task Post_MenorSemResponsavel_Retorna400ComNomeEEmail_ENaoCria()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var antes = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync());

        var erros = await ErrosAsync(await PostAsync(client, Menor()));

        Assert.Contains("menor de 18 anos", (string)erros["ResponsavelNome"]![0]!);
        Assert.Contains("menor de 18 anos", (string)erros["ResponsavelEmail"]![0]!);
        Assert.False(erros.ContainsKey("ResponsavelCpf"));
        Assert.Equal(antes, await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync()));
    }

    [Fact]
    public async Task Post_MenorComResponsavelSoComEspacos_ContaComoAusente()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var erros = await ErrosAsync(await PostAsync(client, Menor(("responsavelNome", "   "), ("responsavelEmail", ""))));

        Assert.True(erros.ContainsKey("ResponsavelNome"));
        Assert.True(erros.ContainsKey("ResponsavelEmail"));
    }

    [Fact]
    public async Task Post_MenorComResponsavel_Cria_ComTudoNormalizado()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var cpf = TestHelpers.NovoCpf();

        var response = await PostAsync(client, Menor(
            ("responsavelNome", "  Maria da Conceição D'Ávila  "), ("responsavelEmail", "  maria.responsavel@local.dev "),
            ("responsavelCpf", $" {TestHelpers.MascararCpf(cpf)} "), ("responsavelTelefone", "(21) 98888-7777")));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal("Maria da Conceição D'Ávila", dto.ResponsavelNome);
        Assert.Equal("maria.responsavel@local.dev", dto.ResponsavelEmail);
        Assert.Equal(cpf, dto.ResponsavelCpf);
        Assert.Equal("21988887777", dto.ResponsavelTelefone);

        var salvo = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == dto.Id));
        Assert.Equal("Maria da Conceição D'Ávila", salvo.ResponsavelNome);
        Assert.Equal(cpf, salvo.ResponsavelCpf);
        Assert.Equal("21988887777", salvo.ResponsavelTelefone);
    }

    [Fact]
    public async Task Post_MaiorOuSemDataDeNascimento_NaoExigeResponsavel()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        foreach (var corpo in new[] { Maior(), new Dictionary<string, object?>() })
        {
            var response = await PostAsync(client, corpo);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
            Assert.Null(dto.ResponsavelNome);
            Assert.Null(dto.ResponsavelEmail);
            Assert.Null(dto.ResponsavelCpf);
            Assert.Null(dto.ResponsavelTelefone);
        }
    }

    [Fact]
    public async Task Post_MaiorComResponsavel_AceitaComoOpcional()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var response = await PostAsync(client, Maior(("responsavelNome", "Contato de Emergência"), ("responsavelTelefone", "(11) 3456-7890")));

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal("Contato de Emergência", dto.ResponsavelNome);
        Assert.Null(dto.ResponsavelEmail);
        Assert.Equal("1134567890", dto.ResponsavelTelefone);
    }

    [Fact]
    public async Task Post_FronteiraDos18Anos_ComparaNoNivelDoDia()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var hoje = HojeCivil();

        var completaHoje = await PostAsync(client, new Dictionary<string, object?> { ["dataNascimento"] = Data(hoje.AddYears(-18)) });
        Assert.True(completaHoje.StatusCode == HttpStatusCode.Created, await completaHoje.Content.ReadAsStringAsync());

        var completaAmanha = await PostAsync(client, new Dictionary<string, object?> { ["dataNascimento"] = Data(hoje.AddYears(-18).AddDays(1)) });
        Assert.True((await ErrosAsync(completaAmanha)).ContainsKey("ResponsavelNome"));
    }

    public static TheoryData<string, object, string> ResponsavelInvalido => new()
    {
        { "responsavelNome", "Ana 😀", "não suportados" },
        { "responsavelNome", "Łukasz Nowak", "não suportados" },
        { "responsavelNome", new string('a', 201), "no máximo 200" },
        { "responsavelEmail", "nao-e-email", "formato inválido" },
        { "responsavelEmail", "joão@local.dev", "ASCII" },
        { "responsavelEmail", new string('e', 101 - "@local.dev".Length) + "@local.dev", "no máximo 100" },
        { "responsavelCpf", "123.456.789-00", "11 dígitos de um CPF válido" },
        { "responsavelTelefone", "(11) 1234", "DDD e número" },
    };

    [Theory]
    [MemberData(nameof(ResponsavelInvalido))]
    public async Task Post_CampoDoResponsavelInvalido_Retorna400NoCampo(string campo, object valor, string trecho)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var erros = await ErrosAsync(await PostAsync(client, Maior((campo, valor))));

        var chave = char.ToUpperInvariant(campo[0]) + campo[1..];
        Assert.Contains(trecho, (string)erros[chave]![0]!);
    }

    [Fact]
    public async Task Post_CpfDoResponsavelIgualAoDoProprioUsuario_Retorna400()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var cpf = TestHelpers.NovoCpf();

        var erros = await ErrosAsync(await PostAsync(client, Maior(("cpf", cpf), ("responsavelCpf", TestHelpers.MascararCpf(cpf)))));

        Assert.Contains("igual ao cpf do próprio usuário", (string)erros["ResponsavelCpf"]![0]!);
    }

    public static TheoryData<string, string> DadoDoUsuarioInvalido => new()
    {
        { "cpf", "529.982.247-26" },
        { "cpf", "111.111.111-11" },
        { "cpf", "5299822472" },
        { "telefone", "1111-1111" },
        { "telefone", "(23) 98765-4321" },
        { "telefone", "(11) 88765-4321" },
    };

    [Theory]
    [MemberData(nameof(DadoDoUsuarioInvalido))]
    public async Task Post_CpfOuTelefoneDoUsuarioForaDaRegra_Retorna400(string campo, string valor)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);

        var erros = await ErrosAsync(await PostAsync(client, Maior((campo, valor))));

        Assert.True(erros.ContainsKey(char.ToUpperInvariant(campo[0]) + campo[1..]));
    }
}
