using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class UsuariosCinEnderecoSqlServerTests : IClassFixture<EventosSqlFixture>
{
    private const string IpTeste = "203.0.113.57";
    private readonly EventosSqlFixture fx;

    public UsuariosCinEnderecoSqlServerTests(EventosSqlFixture fx)
    {
        this.fx = fx;
        fx.Factory.ViaCep.Reiniciar();
    }

    private HttpClient Client => fx.Client;
    private SqlServerApiFactory Factory => fx.Factory;

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(Factory, consulta);

    private Task<Usuario> UsuarioAsync(Guid id) => Db(db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<List<UsuarioHistorico>> HistoricoAsync(Guid usuarioId) =>
        Db(db => db.UsuariosHistorico.AsNoTracking().Where(h => h.UsuarioId == usuarioId).OrderBy(h => h.AlteradoEmUtc).ToListAsync());

    private async Task<UsuarioListItemDto> CriarAsync(Action<JsonObject>? extras = null)
    {
        var cargoId = await Db(db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());
        var corpo = new JsonObject
        {
            ["nome"] = $"Participante Teste {Guid.NewGuid():N}", ["email"] = $"cin-{Guid.NewGuid():N}@local.dev",
            ["senha"] = $"Forte-{Guid.NewGuid():N}", ["cargoId"] = cargoId.ToString(), ["cpf"] = TestHelpers.NovoCpf(),
        };
        extras?.Invoke(corpo);
        var response = await Client.PostAsJsonAsync("/api/Usuarios", corpo);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
    }

    private Task<HttpResponseMessage> PutAsync(Guid id, JsonObject corpo)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Usuarios/{id}") { Content = JsonContent.Create(corpo) };
        request.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        return Client.SendAsync(request);
    }

    private async Task<UsuarioListItemDto> PutOkAsync(UsuarioListItemDto u, Action<JsonObject>? extras = null)
    {
        var response = await PutAsync(u.Id, CorpoPut(u, extras));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;
    }

    private static JsonObject CorpoPut(UsuarioListItemDto u, Action<JsonObject>? extras = null)
    {
        var corpo = new JsonObject { ["nome"] = u.Nome, ["email"] = u.Email, ["cargoId"] = u.Cargo.Id.ToString() };
        extras?.Invoke(corpo);
        return corpo;
    }

    private static JsonObject Endereco(string? cep = null, string? numero = null, string? complemento = null, bool comCep = true)
    {
        var e = new JsonObject();
        if (comCep) e["cep"] = cep;
        if (numero is not null) e["numero"] = numero;
        if (complemento is not null) e["complemento"] = complemento;
        return e;
    }

    [Fact]
    public async Task Colunas_TiposNulabilidadeDefaultECheck_NasDuasTabelas()
    {
        var colunas = await Db(async db =>
        {
            var conexao = db.Database.GetDbConnection();
            await conexao.OpenAsync();
            await using var comando = conexao.CreateCommand();
            comando.CommandText = """
                SELECT OBJECT_NAME(c.object_id) + '.' + c.name + '=' + t.name + CASE WHEN t.name = 'varchar' THEN '(' + CAST(c.max_length AS varchar(10)) + ')' ELSE '' END
                       + CASE c.is_nullable WHEN 1 THEN ' NULL' ELSE '' END + ISNULL(' DEFAULT ' + dc.definition COLLATE DATABASE_DEFAULT, '')
                FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
                WHERE c.object_id IN (OBJECT_ID('dbo.Usuarios'), OBJECT_ID('dbo._usuarios_hist'))
                  AND (c.name IN ('EhCin', 'DataExpiracao') OR c.name LIKE 'Endereco%')
                """;
            var lista = new List<string>();
            await using var reader = await comando.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lista.Add(reader.GetString(0));
            return lista;
        });

        var enderecos = new[]
        {
            "EnderecoCep=varchar(8)", "EnderecoLogradouro=varchar(200)", "EnderecoNumero=varchar(20)", "EnderecoComplemento=varchar(100)",
            "EnderecoComplementoCep=varchar(200)", "EnderecoUnidade=varchar(200)", "EnderecoBairro=varchar(100)", "EnderecoLocalidade=varchar(100)",
            "EnderecoUf=varchar(2)", "EnderecoEstado=varchar(50)", "EnderecoRegiao=varchar(20)", "EnderecoIbge=varchar(7)",
            "EnderecoGia=varchar(10)", "EnderecoDdd=varchar(2)", "EnderecoSiafi=varchar(10)",
        };
        var esperado = new List<string> { "Usuarios.EhCin=bit DEFAULT (CONVERT([bit],(0)))", "Usuarios.DataExpiracao=date NULL",
            "_usuarios_hist.EhCin=bit NULL", "_usuarios_hist.DataExpiracao=date NULL" };
        esperado.AddRange(enderecos.Select(e => $"Usuarios.{e} NULL"));
        esperado.AddRange(enderecos.Select(e => $"_usuarios_hist.{e} NULL"));
        Assert.Equal(esperado.Order(), colunas.Order());
        Assert.Equal(1, await EventosSqlSupport.EscalarAsync<int>(Factory,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Usuarios_Endereco' AND parent_object_id = OBJECT_ID('dbo.Usuarios')"));
    }

    [Theory]
    [InlineData("EnderecoLogradouro = 'Rua solta'")]
    [InlineData("EnderecoCep = '0438216', EnderecoLocalidade = 'São Paulo', EnderecoUf = 'SP'")]
    [InlineData("EnderecoCep = '0438216A', EnderecoLocalidade = 'São Paulo', EnderecoUf = 'SP'")]
    [InlineData("EnderecoCep = '04382160', EnderecoUf = 'SP'")]
    [InlineData("EnderecoCep = '04382160', EnderecoLocalidade = 'São Paulo', EnderecoUf = 'sp'")]
    public async Task Banco_CheckRecusaEnderecoInconsistente_MesmoSemPassarPelaAplicacao(string set)
    {
        var alvo = await CriarAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var conexao = new SqlConnection(Factory.ConnectionString);
            await conexao.OpenAsync();
            await using var comando = conexao.CreateCommand();
            comando.CommandText = $"""
                EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@resp;
                EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
                UPDATE dbo.Usuarios SET {set} WHERE Id = @id;
                """;
            comando.Parameters.AddWithValue("@resp", fx.AdminId);
            comando.Parameters.AddWithValue("@id", alvo.Id);
            await comando.ExecuteNonQueryAsync();
        });

        Assert.Equal(547, ex.Number);
        Assert.Null((await UsuarioAsync(alvo.Id)).EnderecoCep);
    }

    [Fact]
    public async Task Crud_CinValidadeEEndereco_OmissaoPreserva_AlteracaoSoNosNovosCamposGeraHistoricoComOEstadoAnterior()
    {
        var alvo = await CriarAsync(c =>
        {
            c["ehCin"] = true;
            c["dataExpiracao"] = "2031-05-10";
            c["endereco"] = Endereco("04382-160", "100", "Casa 2");
        });
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(alvo.Endereco, "04382160", "100", "Casa 2");
        Assert.Single(Factory.ViaCep.Consultas);

        // PUT sem os campos novos: nada muda, nenhum histórico, nenhuma consulta ao ViaCEP.
        var semCampos = await PutOkAsync(alvo);
        Assert.True(semCampos.EhCin);
        Assert.Equal(new DateOnly(2031, 5, 10), semCampos.DataExpiracao);
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(semCampos.Endereco, "04382160", "100", "Casa 2");
        Assert.Empty(await HistoricoAsync(alvo.Id));
        Assert.Single(Factory.ViaCep.Consultas);

        // ehCin=false é um valor informado (não omissão): grava false e o histórico guarda o true anterior.
        var semCin = await PutOkAsync(alvo, c => c["ehCin"] = false);
        Assert.False(semCin.EhCin);
        Assert.False((await UsuarioAsync(alvo.Id)).EhCin);
        var historico = await HistoricoAsync(alvo.Id);
        Assert.Single(historico);
        Assert.Equal((true, new DateOnly(2031, 5, 10), "04382160", "100", "Casa 2"),
            (historico[0].EhCin, historico[0].DataExpiracao, historico[0].EnderecoCep, historico[0].EnderecoNumero, historico[0].EnderecoComplemento));
        Assert.Equal((fx.AdminId, AlmiranteApiFactory.AdminEmail, IpTeste, UsuarioHistorico.OperacaoUpdate),
            (historico[0].UsuarioResponsavelId, historico[0].UsuarioResponsavelLogin, historico[0].IpResponsavel, historico[0].TipoOperacao));

        // Omitir ehCin depois disso preserva o false.
        Assert.False((await PutOkAsync(alvo, c => c["dataExpiracao"] = "2019-01-31")).EhCin);
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal((false, new DateOnly(2019, 1, 31)), (salvo.EhCin, salvo.DataExpiracao));

        // Mesmo CEP (com máscara): não consulta o ViaCEP; número muda, complemento omitido é mantido.
        var mesmoCep = await PutOkAsync(alvo, c => c["endereco"] = Endereco("04382-160", "100A"));
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(mesmoCep.Endereco, "04382160", "100A", "Casa 2");
        Assert.Single(Factory.ViaCep.Consultas);

        // CEP novo: consulta, substitui todos os dados do CEP; número informado, complemento omitido fica vazio (era do endereço anterior).
        var outroCep = await PutOkAsync(alvo, c => c["endereco"] = Endereco("01310-200", "1578"));
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(outroCep.Endereco, "01310200", "1578", null);
        Assert.Equal(new[] { "04382160", "01310200" }, Factory.ViaCep.Consultas);
        salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal(("01310200", "Rua Fictícia 01310200", "1578", null, "lado ímpar", "São Paulo", "SP", "3550308"),
            (salvo.EnderecoCep, salvo.EnderecoLogradouro, salvo.EnderecoNumero, salvo.EnderecoComplemento, salvo.EnderecoComplementoCep,
                salvo.EnderecoLocalidade, salvo.EnderecoUf, salvo.EnderecoIbge));

        // Só o complemento, sem cep no objeto: mantém o CEP e o número.
        var complemento = await PutOkAsync(alvo, c => c["endereco"] = Endereco(complemento: "Sala 3", comCep: false));
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(complemento.Endereco, "01310200", "1578", "Sala 3");

        // Listagem traz os mesmos valores do banco.
        var item = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!.Single(u => u.Id == alvo.Id);
        Assert.Equal((false, new DateOnly(2019, 1, 31)), (item.EhCin, item.DataExpiracao));
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(item.Endereco, "01310200", "1578", "Sala 3");

        // endereco null e dataExpiracao null removem.
        var limpo = await PutOkAsync(alvo, c => { c["endereco"] = null; c["dataExpiracao"] = null; });
        Assert.Null(limpo.Endereco);
        Assert.Null(limpo.DataExpiracao);
        salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal((null, null, null, null, null), (salvo.EnderecoCep, salvo.EnderecoLogradouro, salvo.EnderecoLocalidade, salvo.EnderecoUf, salvo.DataExpiracao));

        historico = await HistoricoAsync(alvo.Id);
        Assert.Equal(6, historico.Count);
        Assert.Equal((false, new DateOnly(2031, 5, 10)), (historico[1].EhCin, historico[1].DataExpiracao));
        Assert.Equal(("04382160", "100", "Casa 2"), (historico[2].EnderecoCep, historico[2].EnderecoNumero, historico[2].EnderecoComplemento));
        Assert.Equal(("04382160", "Rua Fictícia 04382160", "100A", "Casa 2", "Jardim de Teste", "SP", "11", "7107"),
            (historico[3].EnderecoCep, historico[3].EnderecoLogradouro, historico[3].EnderecoNumero, historico[3].EnderecoComplemento,
                historico[3].EnderecoBairro, historico[3].EnderecoUf, historico[3].EnderecoDdd, historico[3].EnderecoSiafi));
        Assert.Equal(("01310200", "1578", null), (historico[4].EnderecoCep, historico[4].EnderecoNumero, historico[4].EnderecoComplemento));
        Assert.Equal(("01310200", "Sala 3", new DateOnly(2019, 1, 31)), (historico[5].EnderecoCep, historico[5].EnderecoComplemento, historico[5].DataExpiracao));
        Assert.All(historico.Skip(1), h => Assert.Equal((fx.AdminId, IpTeste), (h.UsuarioResponsavelId, h.IpResponsavel)));
    }

    [Fact]
    public async Task Put_SoNumeroDoEndereco_PersisteEGeraHistorico()
    {
        var alvo = await CriarAsync(c => c["endereco"] = Endereco("04382160", "10"));
        await PutOkAsync(alvo, c => c["endereco"] = Endereco(numero: "11", comCep: false));
        Assert.Equal("11", (await UsuarioAsync(alvo.Id)).EnderecoNumero);
        var historico = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal(("10", "04382160", "Rua Fictícia 04382160"), (historico.EnderecoNumero, historico.EnderecoCep, historico.EnderecoLogradouro));
    }

    [Theory]
    [InlineData("503")]
    [InlineData("lento")]
    [InlineData("rede")]
    [InlineData("""{ "cep": "99999-999", "localidade": "X", "uf": "SP" }""")]
    public async Task Put_TrocaDeCepComViaCepFalhando_Retorna503_ENaoAlteraNada(string falha)
    {
        var alvo = await CriarAsync(c => { c["endereco"] = Endereco("04382160", "100", "Casa"); c["ehCin"] = true; });
        Factory.ViaCep.Responder = UsuariosCinEnderecoTests.Falha(falha);

        var response = await PutAsync(alvo.Id, CorpoPut(alvo, c =>
        {
            c["nome"] = "Nome Que Nao Deve Gravar";
            c["ehCin"] = false;
            c["endereco"] = Endereco("01310-200", "1");
        }));

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
        Assert.EndsWith("Nenhum registro foi alterado.", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["detail"]!);
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal((alvo.Nome, true, "04382160", "100", "Casa", "Rua Fictícia 04382160"),
            (salvo.Nome, salvo.EhCin, salvo.EnderecoCep, salvo.EnderecoNumero, salvo.EnderecoComplemento, salvo.EnderecoLogradouro));
        Assert.Empty(await HistoricoAsync(alvo.Id));
    }

    [Fact]
    public async Task Put_ViaCepForaDoAr_MasCepIgual_GravaSemConsultar()
    {
        var alvo = await CriarAsync(c => c["endereco"] = Endereco("04382160", "100"));
        Factory.ViaCep.Responder = UsuariosCinEnderecoTests.Falha("503");
        var consultas = Factory.ViaCep.Consultas.Count;

        var dto = await PutOkAsync(alvo, c => c["endereco"] = Endereco("04382-160", "200"));

        Assert.Equal("200", dto.Endereco!.Numero);
        Assert.Equal(consultas, Factory.ViaCep.Consultas.Count);
    }

    [Fact]
    public async Task Put_CepInexistente_Retorna400_ENaoAlteraNada()
    {
        var alvo = await CriarAsync(c => c["endereco"] = Endereco("04382160", "100"));
        var response = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["endereco"] = Endereco(ViaCepFalso.CepInexistente, "1")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("endereco.cep não encontrado no ViaCEP",
            (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!["Endereco.Cep"]![0]!);
        Assert.Equal(("04382160", "100"), ((await UsuarioAsync(alvo.Id)).EnderecoCep, (await UsuarioAsync(alvo.Id)).EnderecoNumero));
        Assert.Empty(await HistoricoAsync(alvo.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Put_NumeroSemCep_Retorna400(bool comCepVazio)
    {
        var alvo = await CriarAsync();
        var response = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["endereco"] = Endereco(comCepVazio ? "" : null, "10", comCep: comCepVazio)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("endereco.cep é obrigatório para informar número ou complemento.",
            (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!["Endereco.Cep"]![0]!);
        Assert.Null((await UsuarioAsync(alvo.Id)).EnderecoNumero);
    }

    [Fact]
    public async Task Put_CepVazio_RemoveOEnderecoInteiro()
    {
        var alvo = await CriarAsync(c => c["endereco"] = Endereco("04382160", "100", "Casa"));
        var dto = await PutOkAsync(alvo, c => c["endereco"] = Endereco(""));
        Assert.Null(dto.Endereco);
        var salvo = await UsuarioAsync(alvo.Id);
        Assert.Equal((null, null, null, null), (salvo.EnderecoCep, salvo.EnderecoNumero, salvo.EnderecoComplemento, salvo.EnderecoSiafi));
    }

    [Fact]
    public async Task Put_EhCinNull_Retorna400_ENaoAltera()
    {
        var alvo = await CriarAsync(c => c["ehCin"] = true);
        var response = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["ehCin"] = null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await UsuarioAsync(alvo.Id)).EhCin);
    }

    [Fact]
    public async Task Delete_GuardaNoHistoricoCinValidadeEEndereco()
    {
        var alvo = await CriarAsync(c =>
        {
            c["ehCin"] = true;
            c["dataExpiracao"] = "2030-12-31";
            c["endereco"] = Endereco("04382160", "9", "Fundos");
        });
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Usuarios/{alvo.Id}");
        delete.Headers.Add(AlmiranteApiFactory.TestRemoteIpHeader, IpTeste);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(delete)).StatusCode);

        var h = Assert.Single(await HistoricoAsync(alvo.Id));
        Assert.Equal((UsuarioHistorico.OperacaoDelete, true, new DateOnly(2030, 12, 31), "04382160", "9", "Fundos", "São Paulo"),
            (h.TipoOperacao, h.EhCin, h.DataExpiracao, h.EnderecoCep, h.EnderecoNumero, h.EnderecoComplemento, h.EnderecoLocalidade));
    }
}
