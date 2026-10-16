using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Contrato e validação de CPF, data de nascimento, telefone e e-mail em /api/Usuarios sem SQL Server. Unicidade no
// banco, histórico e PUT: ver UsuariosDadosPessoaisSqlServerTests.
public sealed class UsuariosDadosPessoaisTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private Task<Guid> CargoIdAsync() =>
        TestHelpers.WithDbAsync(factory, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());

    private async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string extras, string? email = null)
    {
        var json = $$"""{"nome":"Fulano","email":"{{email ?? $"f-{Guid.NewGuid():N}@local.dev"}}","senha":"Forte-Senha-9x2k","cargoId":"{{await CargoIdAsync()}}"{{extras}}}""";
        return await client.PostAsync("/api/Usuarios", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static string Amanha => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd");

    public static TheoryData<string, string> ExtrasInvalidos => new()
    {
        { ""","dataNascimento":"AMANHA" """, "DataNascimento" },
        { ""","dataNascimento":"17/05/2001" """, "dataNascimento" },
        { ""","dataNascimento":"2001-05-17T00:00:00" """, "dataNascimento" },
        { ""","dataNascimento":"2001-02-30" """, "dataNascimento" },
        { ""","cpf":"123456789012345678901" """, "Cpf" },
        { ""","cpf":"１２３４５６７８９０１" """, "Cpf" },
        { ""","telefone":"+55 (11) 98765-43210 r" """, "Telefone" },
        { ""","telefone":"(11) 98765‑4321" """, "Telefone" },
    };

    [Theory]
    [MemberData(nameof(ExtrasInvalidos))]
    public async Task Post_DadoPessoalInvalido_Retorna400NoCampo_ENaoCria(string extras, string campo)
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var antes = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync());

        var response = await PostJsonAsync(client, extras.Replace("AMANHA", Amanha).TrimEnd());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        var erros = JsonNode.Parse(corpo)!["errors"]!.AsObject();
        Assert.Contains(erros, e => e.Key.Contains(campo, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("System.", corpo);
        Assert.Equal(antes, await TestHelpers.WithDbAsync(factory, db => db.Usuarios.CountAsync()));
    }

    [Fact]
    public async Task Post_EmailAcimaDe100OuComAcento_Retorna400()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var longo = new string('a', 101 - "@local.dev".Length) + "@local.dev";

        foreach (var email in new[] { longo, "joão@local.dev" })
        {
            var response = await PostJsonAsync(client, "", email);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Email", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject().Select(e => e.Key));
        }
    }

    [Fact]
    public async Task Post_LimitesValidos_SaoAceitos_ComCpfMedidoDepoisDaNormalizacao()
    {
        using var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        // 26 caracteres com máscara, 20 depois de normalizado: dentro do limite.
        var cpfMascarado = $" {Random.Shared.NextInt64(0, 1_000_000_000):D9}.{Random.Shared.NextInt64(0, 1_000_000_000):D9}-{Random.Shared.Next(0, 100):D2} ";
        var email = new string('c', 100 - "@local.dev".Length - 8) + Guid.NewGuid().ToString("N")[..8] + "@local.dev";

        var response = await PostJsonAsync(client, $$""","cpf":"{{cpfMascarado}}","dataNascimento":"{{hoje}}","telefone":"12345678901234567890" """.TrimEnd(), email);

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var dto = (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
        Assert.Equal(20, dto.Cpf!.Length);
        Assert.Equal(UsuariosService.NormalizarCpf(cpfMascarado), dto.Cpf);
        Assert.Equal(hoje, dto.DataNascimento!.Value.ToString("yyyy-MM-dd"));
        Assert.Equal(100, dto.Email.Length);
    }

    [Theory]
    [InlineData("123.456.789-01", "12345678901")]
    [InlineData("  012.345.678-90  ", "01234567890")]
    [InlineData("01234567890", "01234567890")]
    [InlineData("AB-12.3", "AB123")]      // sem exigência de só dígitos nesta demanda
    [InlineData(" . - ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizarCpf_RemoveMascaraEEspacosExternos_PreservaZerosEVazioViraNull(string? entrada, string? esperado) =>
        Assert.Equal(esperado, UsuariosService.NormalizarCpf(entrada));

    [Fact]
    public async Task Login_ComEmailForaDoAscii_Retorna401_SemErroInterno()
    {
        using var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(client, "joão@local.dev", "Qualquer-Senha-1")).StatusCode);
    }
}
