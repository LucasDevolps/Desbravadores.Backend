using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Almirante.Api.Controllers;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Almirante.Api.Validation.Usuarios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Tests;

// Issue #193: PATCH /api/Usuarios/{id}. Contrato, validação e documentação, sem banco; a gravação está em UsuariosEdicaoParcialSqlServerTests.
public sealed class UsuariosEdicaoParcialTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static readonly PatchUsuarioRequestValidator Parcial = new(TimeProvider.System);
    private static readonly UpdateUsuarioRequestValidator Completo = new(TimeProvider.System);

    private static T Ler<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;

    private static List<string> Campos<T>(FluentValidation.IValidator<T> validator, T request) =>
        validator.Validate(request).Errors.Select(e => e.PropertyName).Distinct().ToList();

    [Fact]
    public void Corpo_SoComTelefone_MarcaSoOTelefoneComoInformado()
    {
        var request = Ler<PatchUsuarioRequest>("""{"telefone":"(11) 98765-4321"}""");

        Assert.Equal((false, false, false, true, true), (request.NomeInformado, request.EmailInformado, request.CargoIdInformado,
            request.TelefoneInformado, request.AlgumCampoInformado));
        Assert.Empty(Campos(Parcial, request));
    }

    [Fact]
    public void NullExplicito_EhInformado_EOmissaoNao()
    {
        var comNull = Ler<PatchUsuarioRequest>("""{"nome":null,"email":null,"cargoId":null}""");
        var vazio = Ler<PatchUsuarioRequest>("{}");

        Assert.Equal((true, true, true, true), (comNull.NomeInformado, comNull.EmailInformado, comNull.CargoIdInformado, comNull.AlgumCampoInformado));
        Assert.Equal((false, false, false, false, false), (vazio.NomeInformado, vazio.EmailInformado, vazio.CargoIdInformado,
            vazio.AlgumDadoPessoalInformado, vazio.AlgumCampoInformado));
    }

    [Theory]
    [InlineData("""{"cpf":null}""")]
    [InlineData("""{"dataNascimento":null}""")]
    [InlineData("""{"responsavelNome":null}""")]
    [InlineData("""{"responsavelEmail":null}""")]
    [InlineData("""{"responsavelCpf":null}""")]
    [InlineData("""{"responsavelTelefone":null}""")]
    [InlineData("""{"rg":null}""")]
    [InlineData("""{"tipoSanguineo":null}""")]
    [InlineData("""{"doencasAlergias":null}""")]
    [InlineData("""{"ehCin":false}""")]
    [InlineData("""{"dataExpiracao":null}""")]
    [InlineData("""{"endereco":null}""")]
    [InlineData("""{"nome":"Só o nome"}""")]
    [InlineData("""{"email":"so-email@local.dev"}""")]
    [InlineData("""{"cargoId":"11111111-1111-4111-8111-111111111111"}""")]
    public void QualquerCampoSozinho_ContaComoAlteracao_EEhValido(string json)
    {
        var request = Ler<PatchUsuarioRequest>(json);

        Assert.True(request.AlgumCampoInformado);
        Assert.Empty(Campos(Parcial, request));
    }

    [Fact]
    public void CorpoSemCampos_EhRecusadoEmCorpo()
    {
        var erro = Assert.Single(Parcial.Validate(Ler<PatchUsuarioRequest>("{}")).Errors);

        Assert.Equal((PatchUsuarioRequestValidator.CampoCorpo, PatchUsuarioRequestValidator.CorpoVazio), (erro.PropertyName, erro.ErrorMessage));
    }

    [Theory]
    [InlineData("""{"nome":null}""", "Nome", "nome é obrigatório (omita o campo para manter o valor atual).")]
    [InlineData("""{"nome":"   "}""", "Nome", "nome é obrigatório (omita o campo para manter o valor atual).")]
    [InlineData("""{"email":""}""", "Email", "email é obrigatório (omita o campo para manter o valor atual).")]
    [InlineData("""{"email":"sem-arroba"}""", "Email", "email em formato inválido.")]
    [InlineData("""{"cargoId":null}""", "CargoId", "cargoId é obrigatório (omita o campo para manter o valor atual).")]
    [InlineData("""{"cargoId":"00000000-0000-0000-0000-000000000000"}""", "CargoId", "cargoId é obrigatório (omita o campo para manter o valor atual).")]
    [InlineData("""{"telefone":"123"}""", "Telefone", null)]
    [InlineData("""{"ehCin":null}""", "EhCin", null)]
    public void CampoEnviado_SegueAsRegrasDoPut_ENomeEmailCargoNaoAceitamNull(string json, string campo, string? mensagem)
    {
        var erros = Parcial.Validate(Ler<PatchUsuarioRequest>(json)).Errors;

        var erro = Assert.Single(erros);
        Assert.Equal(campo, erro.PropertyName);
        if (mensagem is not null)
        {
            Assert.Equal(mensagem, erro.ErrorMessage);
        }
    }

    [Fact]
    public void Put_ContinuaExigindoNomeEmailECargo_ComAMensagemDeAntes()
    {
        var erros = Completo.Validate(Ler<UpdateUsuarioRequest>("""{"telefone":"11987654321"}""")).Errors;

        Assert.Equal(["nome é obrigatório.", "email é obrigatório.", "cargoId é obrigatório."], erros.Select(e => e.ErrorMessage));
    }

    [Fact]
    public async Task Http_CorpoVazioOuNomeNull_Retorna400SemChegarAoServico()
    {
        var (admin, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "ADM");
        var alvo = await TestHelpers.AddUsuarioAsync(factory, "Alvo do patch", "DS");

        var vazio = await admin.PatchAsJsonAsync($"/api/Usuarios/{alvo.Id}", new { });
        var semNome = await admin.PatchAsJsonAsync($"/api/Usuarios/{alvo.Id}", new { nome = (string?)null, telefone = "11987654321" });

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (vazio.StatusCode, semNome.StatusCode));
        Assert.Equal([PatchUsuarioRequestValidator.CorpoVazio],
            (await vazio.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors[PatchUsuarioRequestValidator.CampoCorpo]);
        var problema = (await semNome.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Equal(["Nome"], problema.Errors.Keys);
    }

    [Fact]
    public async Task Controller_SemIdDeUsuarioNaIdentidade_Retorna401SemEnviarNadaAoMediator()
    {
        // O PermissaoAuthorizationHandler já barra essa identidade; o controller confere de novo e não executa nada em nome de ninguém.
        var controller = new UsuariosController(null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "DIR")], "Teste")) },
            },
        };

        var patch = await controller.Patch(Guid.NewGuid(), Ler<PatchUsuarioRequest>("""{"telefone":"11987654321"}"""), CancellationToken.None);
        var put = await controller.Update(Guid.NewGuid(), new UpdateUsuarioRequest(), CancellationToken.None);

        Assert.All(new[] { patch, put }, r => Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<UnauthorizedObjectResult>(r).StatusCode));
    }

    [Fact]
    public async Task Swagger_DocumentaOPatchComExemploMinimo_EMantemOPut()
    {
        using var client = factory.CreateClient();

        var caminho = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"))!["paths"]!["/api/Usuarios/{id}"]!;
        var patch = caminho["patch"]!;
        var json = patch["requestBody"]!["content"]!["application/json"]!;

        Assert.Contains("mantêm o valor gravado", (string)patch["summary"]!, StringComparison.Ordinal);
        Assert.Contains("O PUT continua disponível", (string)patch["description"]!, StringComparison.Ordinal);
        Assert.Equal("""{"telefone":"(11) 98765-4321"}""", json["examples"]!["soTelefone"]!["value"]!.ToJsonString());
        Assert.Equal("""{"doencasAlergias":null}""", json["examples"]!["removerOpcional"]!["value"]!.ToJsonString());
        Assert.NotNull(patch["responses"]!["200"]);
        Assert.NotNull(caminho["put"]);
        Assert.Null(caminho["put"]!["requestBody"]!["content"]!["application/json"]!["examples"]);
    }
}
