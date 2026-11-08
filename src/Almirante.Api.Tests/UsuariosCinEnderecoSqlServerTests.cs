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
            ["senha"] = $"Forte-{Guid.NewGuid():N}", ["cargoId"] = cargoId.ToString(), ["cpf"] = TestHelpers.NovoCpf(), ["rg"] = TestHelpers.RgFicticio,
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

    [Fact(DisplayName = "CIN, validade e endereço: omissão preserva, ehCin do documento completo não muda e cada alteração grava o estado anterior no histórico")]
    public async Task Crud_CinValidadeEEndereco_OmissaoPreserva_AlteracaoSoNosNovosCamposGeraHistoricoComOEstadoAnterior()
    {
        //Arrange
        var alvo = await CriarAsync(c =>
        {
            c["ehCin"] = true;
            c["dataExpiracao"] = "2031-05-10";
            c["endereco"] = Endereco("04382-160", "100", "Casa 2");
        });
        var consultasAposCadastro = Factory.ViaCep.Consultas.Count;

        //Act
        var semCampos = await PutOkAsync(alvo);
        var historicoSemCampos = await HistoricoAsync(alvo.Id);
        var consultasSemCampos = Factory.ViaCep.Consultas.Count;
        var trocaDeCin = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["ehCin"] = false));
        var salvoAposTrocaDeCin = await UsuarioAsync(alvo.Id);
        var novaValidade = await PutOkAsync(alvo, c => c["dataExpiracao"] = "2019-01-31");
        var salvoAposValidade = await UsuarioAsync(alvo.Id);
        var mesmoCep = await PutOkAsync(alvo, c => c["endereco"] = Endereco("04382-160", "100A"));
        var consultasMesmoCep = Factory.ViaCep.Consultas.Count;
        var outroCep = await PutOkAsync(alvo, c => c["endereco"] = Endereco("01310-200", "1578"));
        var salvoOutroCep = await UsuarioAsync(alvo.Id);
        var complemento = await PutOkAsync(alvo, c => c["endereco"] = Endereco(complemento: "Sala 3", comCep: false));
        var item = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!.Single(u => u.Id == alvo.Id);
        var limpo = await PutOkAsync(alvo, c => { c["endereco"] = null; c["dataExpiracao"] = null; });
        var salvoLimpo = await UsuarioAsync(alvo.Id);
        var historico = await HistoricoAsync(alvo.Id);

        //Assert
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(alvo.Endereco, "04382160", "100", "Casa 2");
        Assert.Equal(1, consultasAposCadastro);

        // PUT sem os campos novos: nada muda, nenhum histórico, nenhuma consulta ao ViaCEP.
        Assert.True(semCampos.EhCin);
        Assert.Equal(new DateOnly(2031, 5, 10), semCampos.DataExpiracao);
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(semCampos.Endereco, "04382160", "100", "Casa 2");
        Assert.Empty(historicoSemCampos);
        Assert.Equal(1, consultasSemCampos);

        // Documento completo (CPF com CIN): ehCin não muda; omitido, continua true.
        await AssertDocumentoTravadoAsync(trocaDeCin, "EhCin");
        Assert.True(salvoAposTrocaDeCin.EhCin);
        Assert.True(novaValidade.EhCin);
        Assert.Equal((true, new DateOnly(2019, 1, 31)), (salvoAposValidade.EhCin, salvoAposValidade.DataExpiracao));

        // Mesmo CEP (com máscara): não consulta o ViaCEP; número muda, complemento omitido é mantido.
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(mesmoCep.Endereco, "04382160", "100A", "Casa 2");
        Assert.Equal(1, consultasMesmoCep);

        // CEP novo: consulta, substitui todos os dados do CEP; número informado, complemento omitido fica vazio (era do endereço anterior).
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(outroCep.Endereco, "01310200", "1578", null);
        Assert.Equal(new[] { "04382160", "01310200" }, Factory.ViaCep.Consultas);
        Assert.Equal(("01310200", "Rua Fictícia 01310200", "1578", null, "lado ímpar", "São Paulo", "SP", "3550308"),
            (salvoOutroCep.EnderecoCep, salvoOutroCep.EnderecoLogradouro, salvoOutroCep.EnderecoNumero, salvoOutroCep.EnderecoComplemento,
                salvoOutroCep.EnderecoComplementoCep, salvoOutroCep.EnderecoLocalidade, salvoOutroCep.EnderecoUf, salvoOutroCep.EnderecoIbge));

        // Só o complemento, sem cep no objeto: mantém o CEP e o número; a listagem traz os mesmos valores do banco.
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(complemento.Endereco, "01310200", "1578", "Sala 3");
        Assert.Equal((true, new DateOnly(2019, 1, 31)), (item.EhCin, item.DataExpiracao));
        UsuariosCinEnderecoTests.AssertEnderecoExemplo(item.Endereco, "01310200", "1578", "Sala 3");

        // endereco null e dataExpiracao null removem.
        Assert.Null(limpo.Endereco);
        Assert.Null(limpo.DataExpiracao);
        Assert.Equal((null, null, null, null, null),
            (salvoLimpo.EnderecoCep, salvoLimpo.EnderecoLogradouro, salvoLimpo.EnderecoLocalidade, salvoLimpo.EnderecoUf, salvoLimpo.DataExpiracao));

        Assert.Equal(5, historico.Count);
        Assert.Equal((true, new DateOnly(2031, 5, 10), "04382160", "100", "Casa 2"),
            (historico[0].EhCin, historico[0].DataExpiracao, historico[0].EnderecoCep, historico[0].EnderecoNumero, historico[0].EnderecoComplemento));
        Assert.Equal((fx.AdminId, AlmiranteApiFactory.AdminEmail, IpTeste, UsuarioHistorico.OperacaoUpdate),
            (historico[0].UsuarioResponsavelId, historico[0].UsuarioResponsavelLogin, historico[0].IpResponsavel, historico[0].TipoOperacao));
        Assert.Equal(("04382160", "100", "Casa 2"), (historico[1].EnderecoCep, historico[1].EnderecoNumero, historico[1].EnderecoComplemento));
        Assert.Equal(("04382160", "Rua Fictícia 04382160", "100A", "Casa 2", "Jardim de Teste", "SP", "11", "7107"),
            (historico[2].EnderecoCep, historico[2].EnderecoLogradouro, historico[2].EnderecoNumero, historico[2].EnderecoComplemento,
                historico[2].EnderecoBairro, historico[2].EnderecoUf, historico[2].EnderecoDdd, historico[2].EnderecoSiafi));
        Assert.Equal(("01310200", "1578", null), (historico[3].EnderecoCep, historico[3].EnderecoNumero, historico[3].EnderecoComplemento));
        Assert.Equal(("01310200", "Sala 3", new DateOnly(2019, 1, 31)), (historico[4].EnderecoCep, historico[4].EnderecoComplemento, historico[4].DataExpiracao));
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

    private const string RgObrigatorio = "rg é obrigatório quando ehCin é false; com ehCin true (documento é a CIN, que usa o CPF) o rg é opcional.";

    private async Task AssertRgObrigatorioAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        Assert.Equal(RgObrigatorio, (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!["Rg"]![0]!);
    }

    private static async Task AssertDocumentoTravadoAsync(HttpResponseMessage response, params string[] campos)
    {
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, corpo);
        var erros = JsonNode.Parse(corpo)!["errors"]!.AsObject();
        Assert.Equal(campos, erros.Select(e => e.Key));
        Assert.All(erros, e => Assert.EndsWith("não pode ser alterado: o documento do usuário (CPF com RG ou com CIN) já está cadastrado.", (string)e.Value![0]!));
    }

    [Fact(DisplayName = "Documento completo com RG: o RG não é removido nem trocado pela CIN e nada vai para o histórico")]
    public async Task Usuarios_Put_DocumentoCompletoComRg_NaoRemoveORgNemPassaParaCin()
    {
        //Arrange
        var alvo = await CriarAsync(c => c["rg"] = "12.345.678-9");

        //Act
        var semRg = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["rg"] = ""));
        var semRgESemCin = await PutAsync(alvo.Id, CorpoPut(alvo, c => { c["rg"] = null; c["ehCin"] = false; }));
        var paraCinSemRg = await PutAsync(alvo.Id, CorpoPut(alvo, c => { c["ehCin"] = true; c["rg"] = ""; }));
        var paraCinMantendoRg = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["ehCin"] = true));
        var salvo = await UsuarioAsync(alvo.Id);
        var historico = await HistoricoAsync(alvo.Id);

        //Assert
        Assert.False(alvo.EhCin);
        await AssertRgObrigatorioAsync(semRg);
        await AssertRgObrigatorioAsync(semRgESemCin);
        await AssertDocumentoTravadoAsync(paraCinSemRg, "Rg", "EhCin");
        await AssertDocumentoTravadoAsync(paraCinMantendoRg, "EhCin");
        Assert.Equal(("12.345.678-9", false), (salvo.Rg, salvo.EhCin));
        Assert.Empty(historico);
    }

    [Fact(DisplayName = "Documento completo com CIN: sair da CIN sem RG exige RG, com RG é recusado e os demais campos continuam editáveis")]
    public async Task Usuarios_Put_DocumentoCompletoComCin_NaoSaiDaCinMasEditaOutrosCampos()
    {
        //Arrange
        var alvo = await CriarAsync(c => { c["ehCin"] = true; c.Remove("rg"); });

        //Act
        var semCinSemRg = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["ehCin"] = false));
        var semCinComRg = await PutAsync(alvo.Id, CorpoPut(alvo, c => { c["ehCin"] = false; c["rg"] = "MG-98.765.432"; }));
        var comCinInformandoRg = await PutAsync(alvo.Id, CorpoPut(alvo, c => c["rg"] = "MG-98.765.432"));
        var outroCampo = await PutOkAsync(alvo, c => c["nome"] = "Nome Editado Com CIN");
        var salvo = await UsuarioAsync(alvo.Id);

        //Assert
        Assert.Null(alvo.Rg);
        await AssertRgObrigatorioAsync(semCinSemRg);
        await AssertDocumentoTravadoAsync(semCinComRg, "Rg", "EhCin");
        await AssertDocumentoTravadoAsync(comCinInformandoRg, "Rg");
        Assert.Equal("Nome Editado Com CIN", outroCampo.Nome);
        Assert.Equal((true, (string?)null), (salvo.EhCin, salvo.Rg));
    }

    [Fact(DisplayName = "Documento completo: nem o Administrador troca o CPF, e reenviar CPF com máscara, RG e ehCin gravados é aceito")]
    public async Task Usuarios_Put_DocumentoCompleto_RecusaTrocarCpfEAceitaReenviarOsValoresGravados()
    {
        //Arrange
        var alvo = await CriarAsync(c => c["rg"] = "12.345.678-9");
        var novoCpf = TestHelpers.NovoCpf();

        //Act
        var trocaDeCpf = await PutAsync(alvo.Id, CorpoPut(alvo, c => { c["cpf"] = novoCpf; c["telefone"] = "(11) 3456-7890"; }));
        var salvoAposRecusa = await UsuarioAsync(alvo.Id);
        var historicoAposRecusa = await HistoricoAsync(alvo.Id);
        var mesmosValores = await PutOkAsync(alvo, c =>
        {
            c["cpf"] = TestHelpers.MascararCpf(alvo.Cpf!);
            c["rg"] = "12.345.678-9";
            c["ehCin"] = false;
            c["telefone"] = "(11) 3456-7890";
        });

        //Assert
        await AssertDocumentoTravadoAsync(trocaDeCpf, "Cpf");
        Assert.Equal((alvo.Cpf, (string?)null), (salvoAposRecusa.Cpf, salvoAposRecusa.Telefone));
        Assert.Empty(historicoAposRecusa);
        Assert.Equal((alvo.Cpf, "12.345.678-9", false, "1134567890"), (mesmosValores.Cpf, mesmosValores.Rg, mesmosValores.EhCin, mesmosValores.Telefone));
    }

    [Fact]
    public async Task Put_CadastroAntigoSemRgESemCin_ContinuaEditavel_MasAoMexerNoDocumentoExigeRg()
    {
        var legado = await TestHelpers.AddUsuarioAsync(Factory, "Legado Sem Documento", "DS");
        var dto = (await Client.GetFromJsonAsync<List<UsuarioListItemDto>>("/api/Usuarios"))!.Single(u => u.Id == legado.Id);
        Assert.Equal((false, null), (dto.EhCin, dto.Rg));

        var editado = await PutOkAsync(dto, c => { c["nome"] = "Legado Editado"; c["telefone"] = "(11) 3456-7890"; });
        Assert.Equal(("Legado Editado", false, null), (editado.Nome, editado.EhCin, editado.Rg));

        await AssertRgObrigatorioAsync(await PutAsync(dto.Id, CorpoPut(editado, c => c["ehCin"] = false)));
        await AssertRgObrigatorioAsync(await PutAsync(dto.Id, CorpoPut(editado, c => c["rg"] = "")));

        Assert.Equal("1234567", (await PutOkAsync(editado, c => c["rg"] = "1234567")).Rg);
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

    [Fact]
    public async Task Put_MesmoCepDoPedido_ComCepGravadoAlteradoNoMeio_ConsultaDeNovoDentroDaTransacao()
    {
        var pausa = new PausaDeComandoInterceptor();
        using var fabrica = new SqlServerApiFactory(configureDb: o => o.AddInterceptors(pausa));
        using var cliente = await TestHelpers.CreateAuthenticatedClientAsync(fabrica);
        var cargoId = await TestHelpers.WithDbAsync(fabrica, db => db.Cargos.Where(c => c.Role == "DS").Select(c => c.Id).SingleAsync());
        var criado = await cliente.PostAsJsonAsync("/api/Usuarios", new JsonObject
        {
            ["nome"] = "Participante CEP", ["email"] = $"cep-{Guid.NewGuid():N}@local.dev", ["senha"] = $"Forte-{Guid.NewGuid():N}",
            ["cargoId"] = cargoId.ToString(), ["cpf"] = TestHelpers.NovoCpf(), ["rg"] = TestHelpers.RgFicticio, ["endereco"] = Endereco("04382160", "100"),
        });
        Assert.True(criado.StatusCode == HttpStatusCode.Created, await criado.Content.ReadAsStringAsync());
        var alvo = (await criado.Content.ReadFromJsonAsync<UsuarioListItemDto>())!;

        var gate = pausa.Armar(sql => sql.Contains("dbo.Usuarios WITH (XLOCK, ROWLOCK)", StringComparison.Ordinal));
        var put = cliente.PutAsJsonAsync($"/api/Usuarios/{alvo.Id}", CorpoPut(alvo, c => c["endereco"] = Endereco("04382160", "200")));
        await gate.Atingida;
        await using (var conexao = new Microsoft.Data.SqlClient.SqlConnection(fabrica.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = conexao.CreateCommand();
            comando.CommandText = """
                EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@id;
                EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
                UPDATE dbo.Usuarios SET EnderecoCep = '01310200' WHERE Id = @id;
                """;
            comando.Parameters.AddWithValue("@id", alvo.Id);
            Assert.Equal(1, await comando.ExecuteNonQueryAsync());
        }
        gate.Liberar();

        var resposta = await put;
        Assert.True(resposta.StatusCode == HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var gravado = await TestHelpers.WithDbAsync(fabrica, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == alvo.Id));
        Assert.Equal(("04382160", "200"), (gravado.EnderecoCep, gravado.EnderecoNumero));
        Assert.Equal("Rua Fictícia 04382160", gravado.EnderecoLogradouro);
    }
}
