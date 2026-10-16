using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

// Dados do responsável (issues #112 e #131) contra SQL Server REAL: no PUT a regra usa os valores EFETIVOS (o que não veio no
// corpo é lido do banco sob o lock), o trigger audita os campos novos e as colunas são varchar com o tamanho exato.
[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosResponsavelSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private const string IpTeste = "203.0.113.41";
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private static DateOnly HojeCivil() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);
    private static string Menor => HojeCivil().AddYears(-13).ToString("yyyy-MM-dd");
    private static string Maior => HojeCivil().AddYears(-40).ToString("yyyy-MM-dd");

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<Guid> CargoDsAsync() => Db(db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());

    private async Task<UsuarioListItemDto> CriarAsync(string? nascimento, string? responsavelNome = null, string? responsavelEmail = null,
        string? cpf = null)
    {
        var response = await Client.PostAsJsonAsync("/api/Usuarios", new
        {
            nome = $"Responsável SQL {Guid.NewGuid():N}", email = $"resp-{Guid.NewGuid():N}@local.dev", senha = $"Forte-{Guid.NewGuid():N}",
            cargoId = await CargoDsAsync(), dataNascimento = nascimento, responsavelNome, responsavelEmail, cpf,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
    }

    private Task<HttpResponseMessage> PutAsync(Guid id, JsonObject corpo)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Usuarios/{id}") { Content = JsonContent.Create(corpo) };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return Client.SendAsync(request);
    }

    private static JsonObject CorpoPut(UsuarioListItemDto u, Action<JsonObject>? extras = null)
    {
        var corpo = new JsonObject { ["nome"] = u.Nome, ["email"] = u.Email, ["cargoId"] = u.Cargo.Id.ToString() };
        extras?.Invoke(corpo);
        return corpo;
    }

    private Task<Usuario> UsuarioAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid id) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == id).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private static async Task<JsonObject> ErrosAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject();
    }

    [Fact]
    public async Task Put_MenorComResponsavelGravado_OmitindoOsCampos_PreservaTudo_SemHistorico()
    {
        var menor = await CriarAsync(Menor, "Joana Mãe", "joana@local.dev");

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(menor.Id, CorpoPut(menor))).StatusCode);

        var salvo = await UsuarioAsync(menor.Id);
        Assert.Equal("Joana Mãe", salvo.ResponsavelNome);
        Assert.Equal("joana@local.dev", salvo.ResponsavelEmail);
        Assert.Empty(await HistoricoAsync(menor.Id));
    }

    [Theory]
    [InlineData("responsavelNome", "ResponsavelNome")]
    [InlineData("responsavelEmail", "ResponsavelEmail")]
    public async Task Put_MenorRemovendoNomeOuEmailDoResponsavel_Retorna400_SemAlterar(string campo, string erro)
    {
        var menor = await CriarAsync(Menor, "Joana Mãe", "joana@local.dev");

        // Sem dataNascimento no corpo: vale a gravada (menor).
        var erros = await ErrosAsync(await PutAsync(menor.Id, CorpoPut(menor, c => c[campo] = null)));

        Assert.True(erros.ContainsKey(erro));
        var salvo = await UsuarioAsync(menor.Id);
        Assert.Equal("Joana Mãe", salvo.ResponsavelNome);
        Assert.Equal("joana@local.dev", salvo.ResponsavelEmail);
        Assert.Empty(await HistoricoAsync(menor.Id));
    }

    [Fact]
    public async Task Put_MaiorQueOCorpoTornaMenor_ExigeResponsavel_EComResponsavelGrava()
    {
        var adulto = await CriarAsync(Maior);

        var sem = await ErrosAsync(await PutAsync(adulto.Id, CorpoPut(adulto, c => c["dataNascimento"] = Menor)));
        Assert.True(sem.ContainsKey("ResponsavelNome"));
        Assert.Equal(DateOnly.Parse(Maior), (await UsuarioAsync(adulto.Id)).DataNascimento);

        var com = await PutAsync(adulto.Id, CorpoPut(adulto, c =>
        {
            c["dataNascimento"] = Menor;
            c["responsavelNome"] = "Carlos Pai";
            c["responsavelEmail"] = "carlos@local.dev";
        }));
        Assert.True(com.StatusCode == HttpStatusCode.OK, await com.Content.ReadAsStringAsync());
        Assert.Equal("Carlos Pai", (await UsuarioAsync(adulto.Id)).ResponsavelNome);
    }

    [Fact]
    public async Task Put_MaiorSemResponsavel_PodeOmitirOuLimparOsCampos()
    {
        var adulto = await CriarAsync(Maior, "Contato", "contato@local.dev");

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(adulto.Id, CorpoPut(adulto))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(adulto.Id, CorpoPut(adulto, c =>
        {
            c["responsavelNome"] = null;
            c["responsavelEmail"] = "";
        }))).StatusCode);

        var salvo = await UsuarioAsync(adulto.Id);
        Assert.Null(salvo.ResponsavelNome);
        Assert.Null(salvo.ResponsavelEmail);
    }

    [Fact]
    public async Task Put_CpfDoResponsavelIgualAoCpfGravadoDoProprioUsuario_Retorna400()
    {
        var cpf = TestHelpers.NovoCpf();
        var adulto = await CriarAsync(Maior, cpf: cpf);

        // cpf omitido no corpo: compara com o gravado.
        var erros = await ErrosAsync(await PutAsync(adulto.Id, CorpoPut(adulto, c => c["responsavelCpf"] = TestHelpers.MascararCpf(cpf))));

        Assert.True(erros.ContainsKey("ResponsavelCpf"));
        Assert.Null((await UsuarioAsync(adulto.Id)).ResponsavelCpf);
    }

    [Fact]
    public async Task Put_MudarSoODadoDoResponsavel_GeraHistoricoComOValorAnterior_EAcentosSobrevivemNoVarchar()
    {
        var menor = await CriarAsync(Menor, "Ângela Conceição D'Ávila", "angela@local.dev");
        var cpf = TestHelpers.NovoCpf();

        var response = await PutAsync(menor.Id, CorpoPut(menor, c =>
        {
            c["responsavelEmail"] = "angela.nova@local.dev";
            c["responsavelCpf"] = cpf;
            c["responsavelTelefone"] = "(31) 99876-5432";
        }));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var salvo = await UsuarioAsync(menor.Id);
        Assert.Equal("Ângela Conceição D'Ávila", salvo.ResponsavelNome);   // varchar na página 1252, sem "?"
        Assert.Equal("angela.nova@local.dev", salvo.ResponsavelEmail);
        Assert.Equal(cpf, salvo.ResponsavelCpf);
        Assert.Equal("31998765432", salvo.ResponsavelTelefone);

        var linha = Assert.Single(await HistoricoAsync(menor.Id));
        Assert.Equal("Ângela Conceição D'Ávila", linha.ResponsavelNome);
        Assert.Equal("angela@local.dev", linha.ResponsavelEmail);
        Assert.Null(linha.ResponsavelCpf);
        Assert.Null(linha.ResponsavelTelefone);
        Assert.Null(linha.FotoCaminho);
        Assert.Equal(IpTeste, linha.IpResponsavel);
        Assert.Equal(fx.AdminId, linha.UsuarioResponsavelId);
    }

    [Fact]
    public async Task ColunasNovas_SaoVarcharComOTamanhoExato_ENenhumaEhNvarchar()
    {
        var colunas = await EventosSqlSupport.EscalarAsync<string>(Factory, """
            SELECT STRING_AGG(CONVERT(nvarchar(max), OBJECT_NAME(c.object_id) + '.' + c.name + '=' + t.name + '(' + CAST(c.max_length AS varchar(10)) + ')'), ',')
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id IN (OBJECT_ID('dbo.Usuarios'), OBJECT_ID('dbo._usuarios_hist'))
              AND c.name IN ('ResponsavelNome', 'ResponsavelEmail', 'ResponsavelCpf', 'ResponsavelTelefone', 'FotoCaminho')
            """);

        var esperado = new[]
        {
            "Usuarios.FotoCaminho=varchar(79)", "Usuarios.ResponsavelCpf=varchar(11)", "Usuarios.ResponsavelEmail=varchar(100)",
            "Usuarios.ResponsavelNome=varchar(200)", "Usuarios.ResponsavelTelefone=varchar(11)",
            "_usuarios_hist.FotoCaminho=varchar(79)", "_usuarios_hist.ResponsavelCpf=varchar(11)", "_usuarios_hist.ResponsavelEmail=varchar(100)",
            "_usuarios_hist.ResponsavelNome=varchar(200)", "_usuarios_hist.ResponsavelTelefone=varchar(11)",
        };
        Assert.Equal(esperado.Order(StringComparer.Ordinal), colunas!.Split(',').Order(StringComparer.Ordinal));
    }
}
