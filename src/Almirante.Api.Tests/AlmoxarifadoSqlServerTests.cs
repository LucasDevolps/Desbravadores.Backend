using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

public sealed class AlmoxarifadoSqlFixture : IAsyncLifetime
{
    public const string Ip = "203.0.113.126";

    public SqlServerApiFactory Factory { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = new SqlServerApiFactory();
        Admin = await TestHelpers.CreateAuthenticatedClientAsync(Factory);
        Admin.DefaultRequestHeaders.Add(AlmiranteApiFactory.TestRemoteIpHeader, Ip);
    }

    public Task DisposeAsync()
    {
        Admin.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Issue #126 contra SQL Server real (transação, UPDLOCK, rowversion, índices únicos filtrados e triggers): cadastro e movimentação de bens,
/// inventário imutável, lista de desejos com atendimentos idempotentes, permissões por cargo e designação, concorrência e auditoria.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class AlmoxarifadoSqlServerTests(AlmoxarifadoSqlFixture fx) : IClassFixture<AlmoxarifadoSqlFixture>
{
    private static readonly DateOnly Hoje = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    private HttpClient Admin => fx.Admin;

    private static string D(DateOnly data) => data.ToString("yyyy-MM-dd");

    private static string Categoria() => $"T-{Guid.NewGuid():N}"[..30];

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(fx.Factory, consulta);

    private static async Task<JsonObject> Esperar(HttpResponseMessage resposta, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.True(resposta.StatusCode == esperado, $"esperado {(int)esperado}, obtido {(int)resposta.StatusCode}: {corpo}");
        return corpo.Length == 0 ? [] : JsonNode.Parse(corpo) as JsonObject ?? new JsonObject { ["items"] = JsonNode.Parse(corpo) };
    }

    private static async Task<JsonArray> Lista(HttpResponseMessage resposta) => (await Esperar(resposta))["items"]!.AsArray();

    private static async Task Status(HttpResponseMessage resposta, HttpStatusCode esperado, string? contem = null)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.True(resposta.StatusCode == esperado, $"esperado {(int)esperado}, obtido {(int)resposta.StatusCode}: {corpo}");
        if (contem is not null)
        {
            Assert.Contains(contem, corpo);
        }
    }

    private static Guid Id(JsonNode node) => Guid.Parse((string)node["id"]!);

    private static string Versao(JsonNode node) => (string)node["versao"]!;

    private static Dictionary<string, object?> BemCorpo(string categoria, string nome = "Barraca 4 pessoas", bool comValor = true,
        decimal? valor = 150.50m, string localizacao = "Almoxarifado central", string conservacao = "Bom") => new()
    {
        ["nome"] = nome, ["descricao"] = "Barraca iglu verde", ["categoria"] = categoria, ["estadoConservacao"] = conservacao,
        ["possuiValorComercial"] = comValor, ["valorEstimado"] = comValor ? valor : null, ["dataAquisicao"] = D(Hoje.AddDays(-100)),
        ["origem"] = "Compra", ["localizacao"] = localizacao,
    };

    private async Task<JsonObject> BemAsync(string categoria, string nome = "Barraca 4 pessoas", bool comValor = true, string conservacao = "Bom",
        string localizacao = "Almoxarifado central") =>
        await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", BemCorpo(categoria, nome, comValor, localizacao: localizacao,
            conservacao: conservacao)), HttpStatusCode.Created);

    private async Task<Guid> MembroAsync(string role = "DS") => (await TestHelpers.AddUsuarioAsync(fx.Factory, $"Membro {role}", role)).Id;

    private Task<HttpResponseMessage> EmprestarAsync(Guid bemId, Guid membroId, DateOnly? retirada = null, DateOnly? previsao = null,
        string conservacao = "Bom", HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync($"/api/Almoxarifado/Bens/{bemId}/Emprestimos", new
        {
            membroId, dataRetirada = D(retirada ?? Hoje.AddDays(-2)), previsaoDevolucao = D(previsao ?? Hoje.AddDays(5)), conservacaoSaida = conservacao,
            observacoes = "Acampamento",
        });

    private Task<HttpResponseMessage> DevolverAsync(Guid emprestimoId, DateOnly? data = null, string conservacao = "Bom", bool manutencao = false) =>
        Admin.PostAsJsonAsync($"/api/Almoxarifado/Emprestimos/{emprestimoId}/Devolucao", new
        {
            dataDevolucao = D(data ?? Hoje), conservacaoRetorno = conservacao, encaminharParaManutencao = manutencao,
            motivoManutencao = manutencao ? "Zíper quebrado" : null,
        });

    private async Task<JsonObject> BemAtualAsync(Guid id) => await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Bens/{id}"));

    private static Dictionary<string, object?> DesejoCorpo(string categoria, string nome = "Barraca 4 pessoas", int quantidade = 5,
        object urgencia = null!, string tipo = "Patrimonial", decimal? custo = 380m) => new()
    {
        ["nome"] = nome, ["descricao"] = "Barracas para acampamento", ["justificativa"] = "Acampamento de dezembro", ["tipoItem"] = tipo,
        ["quantidadeDesejada"] = quantidade, ["urgencia"] = urgencia ?? "Urgente", ["categoria"] = categoria, ["custoUnitarioEstimado"] = custo,
        ["dataNecessidade"] = D(Hoje.AddDays(40)), ["linkReferencia"] = "https://exemplo.com/barraca",
    };

    private async Task<JsonObject> DesejoAsync(string categoria, string nome = "Barraca 4 pessoas", int quantidade = 5, object? urgencia = null,
        string tipo = "Patrimonial", decimal? custo = 380m) =>
        await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Desejos", DesejoCorpo(categoria, nome, quantidade, urgencia ?? "Urgente", tipo, custo)),
            HttpStatusCode.Created);

    private static object NovoBem(string nome, string categoria) => new
    {
        nome, descricao = "Recebido no atendimento", categoria, estadoConservacao = "Novo", possuiValorComercial = true, valorEstimado = 380m,
        localizacao = "Almoxarifado central", valorAquisicao = 380m,
    };

    private Task<HttpResponseMessage> AtenderAsync(Guid desejoId, object corpo, Guid? chave = null, HttpClient? client = null)
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Post, $"/api/Almoxarifado/Desejos/{desejoId}/Atendimentos") { Content = JsonContent.Create(corpo) };
        requisicao.Headers.Add("Idempotency-Key", (chave ?? Guid.NewGuid()).ToString());
        return (client ?? Admin).SendAsync(requisicao);
    }

    private Task<int> InativarAsync(Guid usuarioId) => Db(db => db.Database.ExecuteSqlInterpolatedAsync($"""
        EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={usuarioId};
        EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
        UPDATE dbo.Usuarios SET Ativo = 0 WHERE Id = {usuarioId};
        EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
        EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
        """));

    private async Task<int> ScalarAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(fx.Factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CA01_CadastroConsultaEdicaoEExclusaoLogica_ComValidacaoEAuditoria()
    {
        var categoria = Categoria();
        var bem = await BemAsync(categoria, "Lona 6x4");
        var id = Id(bem);
        Assert.Matches("^PAT-\\d{6}$", (string)bem["tombo"]!);
        Assert.Equal(("Disponivel", "Disponível", "Bom", "Compra", "Compra"), ((string)bem["situacao"]!, (string)bem["situacaoDescricao"]!,
            (string)bem["estadoConservacaoDescricao"]!, (string)bem["origem"]!, (string)bem["origemDescricao"]!));
        Assert.Equal("Administrador", (string)bem["criacao"]!["por"]!["nome"]!);

        var obtido = await BemAtualAsync(id);
        Assert.Equal((string)bem["tombo"]!, (string)obtido["tombo"]!);
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Bens/{Guid.NewGuid()}"), HttpStatusCode.NotFound);

        var corpo = BemCorpo(categoria, "Lona 6x4 reforçada");
        corpo["versao"] = Versao(bem);
        corpo["marca"] = "Lonax";
        var atualizado = await Esperar(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{id}", corpo));
        Assert.Equal(("Lona 6x4 reforçada", "Lonax"), ((string)atualizado["nome"]!, (string)atualizado["marca"]!));
        Assert.Equal((string)bem["tombo"]!, (string)atualizado["tombo"]!);
        Assert.NotNull(atualizado["ultimaAlteracao"]);
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{id}", corpo), HttpStatusCode.Conflict, "Versão desatualizada");

        var invalido = BemCorpo(categoria);
        invalido.Remove("nome");
        invalido["localizacao"] = " ";
        var erro = await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", invalido), HttpStatusCode.BadRequest);
        Assert.Equal(["nome é obrigatório."], erro["errors"]!["Nome"]!.AsArray().Select(m => (string)m!));
        Assert.Equal(["localizacao é obrigatório."], erro["errors"]!["Localizacao"]!.AsArray().Select(m => (string)m!));
        var campoDesconhecido = BemCorpo(categoria);
        campoDesconhecido["situacao"] = "Baixado";
        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", campoDesconhecido), HttpStatusCode.BadRequest);

        await Status(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Bens/{id}")
        {
            Content = JsonContent.Create(new { justificativa = "", versao = Versao(atualizado) }),
        }), HttpStatusCode.BadRequest);
        var excluido = await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Bens/{id}")
        {
            Content = JsonContent.Create(new { justificativa = "Cadastro em duplicidade", versao = Versao(atualizado) }),
        }));
        Assert.True((bool)excluido["excluido"]!);
        Assert.Equal("Cadastro em duplicidade", (string)excluido["exclusao"]!["motivo"]!);
        Assert.Empty(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}")));
        Assert.Single(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&incluirExcluidos=true")));
        corpo["versao"] = Versao(excluido);
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{id}", corpo), HttpStatusCode.Conflict, "Bem excluído");
        await Status(await EmprestarAsync(id, await MembroAsync()), HttpStatusCode.Conflict, "Bem excluído");
        await Status(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Bens/{id}")
        {
            Content = JsonContent.Create(new { justificativa = "de novo", versao = Versao(excluido) }),
        }), HttpStatusCode.Conflict, "Bem já excluído");

        var historico = (await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Bens/{id}/Historico")))["items"]!.AsArray();
        Assert.Equal(["Cadastro", "Alteracao", "Exclusao"], historico.Select(h => (string)h!["tipo"]!));
        Assert.All(historico, h => Assert.Equal("Administrador", (string)h!["responsavel"]!["nome"]!));
        Assert.Equal("Cadastro em duplicidade", (string)historico[2]!["justificativa"]!);
        Assert.Null(historico[0]!["estadoAnterior"]);
        Assert.Equal("Lona 6x4", (string)historico[1]!["estadoAnterior"]!["Nome"]!);
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM dbo.almoxarifado_historico WHERE BemId = @id AND IpResponsavel = @ip",
            ("@id", id), ("@ip", AlmoxarifadoSqlFixture.Ip)));
        var porTipo = (await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Bens/{id}/Historico?tipo=Alteracao&de={D(Hoje)}&ate={D(Hoje.AddDays(2))}")))["items"]!;
        Assert.Single(porTipo.AsArray());
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Bens/{id}/Historico?de={D(Hoje)}&ate={D(Hoje.AddDays(-1))}"), HttpStatusCode.BadRequest);
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Bens/{Guid.NewGuid()}/Historico"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CA02_BemComESemValorComercial_TrofeuELivroDeAtasSemData_ComAsRegrasDeEstimativa()
    {
        var categoria = Categoria();
        var comValor = await BemAsync(categoria, "Fogareiro");
        Assert.Equal((true, 150.50m), ((bool)comValor["possuiValorComercial"]!, (decimal)comValor["valorEstimado"]!));

        var trofeu = BemCorpo(categoria, "Troféu Campori 2019", comValor: false);
        trofeu["origem"] = "Outro";
        trofeu["origemDetalhe"] = "Premiação do Campori";
        var criadoTrofeu = await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", trofeu), HttpStatusCode.Created);
        Assert.Equal((false, (decimal?)null, "Premiação do Campori"), ((bool)criadoTrofeu["possuiValorComercial"]!, (decimal?)criadoTrofeu["valorEstimado"],
            (string?)criadoTrofeu["origemDetalhe"]));

        var livro = BemCorpo(categoria, "Livro de atas 1998", comValor: false);
        livro["dataAquisicao"] = null;
        livro["dataAquisicaoDesconhecida"] = true;
        livro["justificativaDataDesconhecida"] = "Acervo recebido da gestão anterior";
        var criadoLivro = await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", livro), HttpStatusCode.Created);
        Assert.Equal((true, (string?)null), ((bool)criadoLivro["dataAquisicaoDesconhecida"]!, (string?)criadoLivro["dataAquisicao"]));

        var casos = new (string Campo, object? Valor, bool? ComValor, string Mensagem)[]
        {
            ("valorEstimado", null, true, "valorEstimado é obrigatório"),
            ("valorEstimado", 10m, false, "valorEstimado deve ser nulo"),
            ("valorEstimado", -1m, true, "valorEstimado deve ser maior ou igual a zero"),
            ("valorEstimado", 1.234m, true, "duas casas decimais"),
            ("valorAquisicao", -0.01m, true, "valorAquisicao deve ser maior ou igual a zero"),
            ("dataAquisicao", null, true, "dataAquisicao é obrigatória"),
            ("origem", "Outro", true, "origemDetalhe é obrigatório"),
            ("origem", 9, true, "origem deve ser um destes valores"),
            ("estadoConservacao", 0, true, "estadoConservacao deve ser um destes valores"),
            ("responsavelGuardaId", Guid.Empty, true, "responsavelGuardaId não pode ser um GUID vazio"),
            ("nome", "Barraca 😀", true, "caracteres não suportados"),
        };
        foreach (var (campo, valor, possui, mensagem) in casos)
        {
            var corpo = BemCorpo(categoria, comValor: possui!.Value);
            corpo[campo] = valor;
            await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", corpo), HttpStatusCode.BadRequest, mensagem);
        }

        var futura = BemCorpo(categoria);
        futura["dataAquisicao"] = D(Hoje.AddDays(5));
        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", futura), HttpStatusCode.BadRequest, "não pode ser futura");
        var semJustificativa = BemCorpo(categoria);
        semJustificativa["dataAquisicao"] = null;
        semJustificativa["dataAquisicaoDesconhecida"] = true;
        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", semJustificativa), HttpStatusCode.BadRequest,
            "justificativaDataDesconhecida é obrigatório");
        var dataEJustificativa = BemCorpo(categoria);
        dataEJustificativa["dataAquisicaoDesconhecida"] = true;
        dataEJustificativa["justificativaDataDesconhecida"] = "sem nota";
        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", dataEJustificativa), HttpStatusCode.BadRequest, "dataAquisicao deve ser nula");
        var guardaInativa = BemCorpo(categoria);
        var inativo = await MembroAsync();
        await InativarAsync(inativo);
        guardaInativa["responsavelGuardaId"] = inativo;
        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", guardaInativa), HttpStatusCode.BadRequest, "responsavelGuardaId deve ser um membro ativo");

        var relatorio = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Relatorios/Patrimonio?categoria={categoria}"));
        Assert.Equal((3, 1, 2, 150.50m), ((int)relatorio["totalBens"]!, (int)relatorio["bensComValorComercial"]!, (int)relatorio["bensSemValorComercial"]!,
            (decimal)relatorio["valorEstimadoPatrimonial"]!));
        Assert.Contains("não é saldo financeiro", (string)relatorio["valorEstimadoPatrimonialRotulo"]!);
    }

    [Fact]
    public async Task CA03_BensDeMesmoNome_SaoRegistrosDistintos_EFiltramPorSituacaoConservacaoELocalizacao()
    {
        var categoria = Categoria();
        var barracas = new List<JsonObject>();
        for (var i = 0; i < 3; i++)
        {
            barracas.Add(await BemAsync(categoria, "Barraca iglu", localizacao: i == 2 ? "Sala da unidade Águia" : "Almoxarifado central",
                conservacao: i == 1 ? "Ruim" : "Bom"));
        }

        Assert.Equal(3, barracas.Select(b => (string)b["tombo"]!).Distinct().Count());
        await Esperar(await EmprestarAsync(Id(barracas[0]), await MembroAsync()), HttpStatusCode.Created);

        Assert.Equal(3, (await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&busca=iglu"))).Count);
        Assert.Equal(Id(barracas[0]), Id((await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&situacao=Emprestado")))[0]!));
        Assert.Equal(2, (await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&situacao=Disponivel"))).Count);
        Assert.Equal(Id(barracas[1]), Id((await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&conservacao=Ruim")))[0]!));
        Assert.Equal(Id(barracas[2]), Id((await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&localizacao=Águia")))[0]!));
        var porTombo = await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?busca={(string)barracas[1]["tombo"]!}"));
        Assert.Equal(Id(barracas[1]), Id(porTombo.Single()!));
        var pagina = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&pageSize=2&page=2"));
        Assert.Equal((3, 2, 1), ((int)pagina["total"]!, (int)pagina["totalPages"]!, pagina["items"]!.AsArray().Count));
        Assert.Single(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}&possuiValorComercial=true&situacao=Emprestado")));
        await Status(await Admin.GetAsync("/api/Almoxarifado/Bens?situacao=7"), HttpStatusCode.BadRequest, "situacao");
        await Status(await Admin.GetAsync("/api/Almoxarifado/Bens?conservacao=Quebrado"), HttpStatusCode.BadRequest);
        await Status(await Admin.GetAsync("/api/Almoxarifado/Bens?pageSize=101"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CA04_Emprestimo_SoDeBemDisponivelEUtilizavel_SemSimultaneidade()
    {
        var categoria = Categoria();
        var membro = await MembroAsync();
        var bem = Id(await BemAsync(categoria));
        var emprestimo = await Esperar(await EmprestarAsync(bem, membro), HttpStatusCode.Created);
        Assert.Equal((true, false, membro), ((bool)emprestimo["aberto"]!, (bool)emprestimo["atrasado"]!, Guid.Parse((string)emprestimo["membro"]!["id"]!)));
        var atual = await BemAtualAsync(bem);
        Assert.Equal(("Emprestado", Id(emprestimo)), ((string)atual["situacao"]!, Id(atual["emprestimoAberto"]!)));
        await Status(await EmprestarAsync(bem, membro), HttpStatusCode.Conflict, "já está emprestado");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Manutencoes", new { dataEntrada = D(Hoje), motivo = "x" }),
            HttpStatusCode.Conflict, "está emprestado");

        var emManutencao = Id(await BemAsync(categoria));
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{emManutencao}/Manutencoes",
            new { dataEntrada = D(Hoje.AddDays(-1)), motivo = "Costura" }), HttpStatusCode.Created);
        await Status(await EmprestarAsync(emManutencao, membro), HttpStatusCode.Conflict, "em manutenção");

        var inservivel = Id(await BemAsync(categoria, conservacao: "Inservivel"));
        await Status(await EmprestarAsync(inservivel, membro), HttpStatusCode.Conflict, "inservível");
        var bom = Id(await BemAsync(categoria));
        await Status(await EmprestarAsync(bom, membro, conservacao: "Inservivel"), HttpStatusCode.Conflict, "inservível");

        var baixado = Id(await BemAsync(categoria));
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{baixado}/Baixa",
            new { dataBaixa = D(Hoje), motivo = "Descarte", justificativa = "Rasgado" }));
        await Status(await EmprestarAsync(baixado, membro), HttpStatusCode.Conflict, "Bem baixado");

        var inativo = await MembroAsync();
        await InativarAsync(inativo);
        await Status(await EmprestarAsync(bom, inativo), HttpStatusCode.BadRequest, "membroId deve ser um membro ativo");
        await Status(await EmprestarAsync(bom, membro, Hoje, Hoje.AddDays(-1)), HttpStatusCode.BadRequest, "previsaoDevolucao não pode ser anterior");
        await Status(await EmprestarAsync(bom, membro, Hoje.AddDays(5), Hoje.AddDays(6)), HttpStatusCode.BadRequest, "dataRetirada não pode ser futura");
        await Status(await EmprestarAsync(bom, membro, Hoje.AddDays(-200)), HttpStatusCode.BadRequest, "anterior à aquisição");
        await Status(await EmprestarAsync(Guid.NewGuid(), membro), HttpStatusCode.NotFound);
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bom}/Emprestimos", new { membroId = membro }), HttpStatusCode.BadRequest,
            "dataRetirada é obrigatória");
    }

    [Fact]
    public async Task CA05_Devolucao_ConservacaoNoRetorno_RepeticaoBloqueada_EAtrasoCalculado()
    {
        var categoria = Categoria();
        var membro = await MembroAsync();
        var bem = Id(await BemAsync(categoria));
        var emprestimo = Id(await Esperar(await EmprestarAsync(bem, membro, Hoje.AddDays(-10), Hoje.AddDays(-5)), HttpStatusCode.Created));

        var aberto = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos/{emprestimo}"));
        Assert.True((bool)aberto["atrasado"]!);
        Assert.True((int)aberto["diasAtraso"]! >= 4);
        var atrasados = await Esperar(await Admin.GetAsync("/api/Almoxarifado/Relatorios/Emprestimos?atrasados=true"));
        Assert.Contains(atrasados["items"]!.AsArray(), e => Id(e!) == emprestimo);
        Assert.Contains((await Lista(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos?atrasados=true&membroId={membro}"))), e => Id(e!) == emprestimo);
        await Status(await Admin.GetAsync("/api/Almoxarifado/Emprestimos?atrasados=true&abertos=false"), HttpStatusCode.BadRequest);

        await Status(await DevolverAsync(emprestimo, Hoje.AddDays(-11)), HttpStatusCode.BadRequest, "anterior à retirada");
        await Status(await DevolverAsync(emprestimo, Hoje.AddDays(3)), HttpStatusCode.BadRequest, "não pode ser futura");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Emprestimos/{emprestimo}/Devolucao",
            new { dataDevolucao = D(Hoje), conservacaoRetorno = "Bom", motivoManutencao = "x" }), HttpStatusCode.BadRequest, "motivoManutencao só é aceito");
        var devolvido = await Esperar(await DevolverAsync(emprestimo, Hoje.AddDays(-1), "Regular"));
        Assert.Equal((false, false, "Devolvido", "Regular"), ((bool)devolvido["aberto"]!, (bool)devolvido["atrasado"]!,
            (string)devolvido["encerramento"]!, (string)devolvido["conservacaoRetorno"]!));
        await Status(await DevolverAsync(emprestimo), HttpStatusCode.Conflict, "já encerrado");
        var bemDevolvido = await BemAtualAsync(bem);
        Assert.Equal(("Disponivel", "Regular", (JsonNode?)null), ((string)bemDevolvido["situacao"]!, (string)bemDevolvido["estadoConservacao"]!,
            bemDevolvido["emprestimoAberto"]));
        Assert.DoesNotContain(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos?abertos=true&bemId={bem}")), e => Id(e!) == emprestimo);
        Assert.Single(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos?abertos=false&bemId={bem}")));

        await Status(await EmprestarAsync(bem, membro, Hoje.AddDays(-3)), HttpStatusCode.BadRequest, "última movimentação");
        var segundo = Id(await Esperar(await EmprestarAsync(bem, membro, Hoje, Hoje.AddDays(3)), HttpStatusCode.Created));
        Assert.False((bool)(await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos/{segundo}")))["atrasado"]!);
        var paraManutencao = await Esperar(await DevolverAsync(segundo, Hoje, "Ruim", manutencao: true));
        Assert.Equal("Ruim", (string)paraManutencao["conservacaoRetorno"]!);
        var emManutencao = await BemAtualAsync(bem);
        Assert.Equal(("EmManutencao", "Zíper quebrado"), ((string)emManutencao["situacao"]!, (string)emManutencao["manutencaoAberta"]!["motivo"]!));
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        await Status(await DevolverAsync(Guid.NewGuid()), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CA06_ManutencaoEBaixaJustificada_PreservamHistorico_ETratamEmprestimoAberto()
    {
        var categoria = Categoria();
        var membro = await MembroAsync();
        var bem = Id(await BemAsync(categoria));
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Manutencoes", new { dataEntrada = D(Hoje) }), HttpStatusCode.BadRequest,
            "motivo é obrigatório");
        var manutencao = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Manutencoes",
            new { dataEntrada = D(Hoje.AddDays(-3)), motivo = "Troca de varetas", observacoes = "Oficina do clube" }), HttpStatusCode.Created);
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Manutencoes", new { dataEntrada = D(Hoje), motivo = "x" }),
            HttpStatusCode.Conflict, "já está em manutenção");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Manutencoes/{Id(manutencao)}/Saida",
            new { dataSaida = D(Hoje.AddDays(-4)), conservacaoSaida = "Bom" }), HttpStatusCode.BadRequest, "anterior à entrada");
        var saida = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Manutencoes/{Id(manutencao)}/Saida",
            new { dataSaida = D(Hoje.AddDays(-1)), conservacaoSaida = "Novo", observacoes = "Varetas novas" }));
        Assert.Equal((false, "Novo"), ((bool)saida["aberta"]!, (string)saida["conservacaoSaida"]!));
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Manutencoes/{Id(manutencao)}/Saida",
            new { dataSaida = D(Hoje), conservacaoSaida = "Bom" }), HttpStatusCode.Conflict, "já encerrada");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Manutencoes/{Guid.NewGuid()}/Saida",
            new { dataSaida = D(Hoje), conservacaoSaida = "Bom" }), HttpStatusCode.NotFound);
        Assert.Equal(("Disponivel", "Novo"), ((string)(await BemAtualAsync(bem))["situacao"]!, (string)(await BemAtualAsync(bem))["estadoConservacao"]!));
        Assert.Single((await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Bens/{bem}/Manutencoes")))["items"]!.AsArray());

        var emprestimo = Id(await Esperar(await EmprestarAsync(bem, membro, Hoje.AddDays(-1), Hoje.AddDays(2)), HttpStatusCode.Created));
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Baixa",
            new { dataBaixa = D(Hoje), motivo = "Descarte", justificativa = "x" }), HttpStatusCode.Conflict, "Encerre o empréstimo aberto");
        await Status(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Bens/{bem}")
        {
            Content = JsonContent.Create(new { justificativa = "tentativa", versao = Versao(await BemAtualAsync(bem)) }),
        }), HttpStatusCode.Conflict, "empréstimo aberto");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Emprestimos/{emprestimo}/PerdaFurto",
            new { data = D(Hoje), motivo = "Devolvido", justificativa = "x" }), HttpStatusCode.BadRequest, "motivo deve ser Perda ou Furto");
        var furto = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Emprestimos/{emprestimo}/PerdaFurto",
            new { data = D(Hoje), motivo = "Furto", justificativa = "Furtada no acampamento (BO 123)" }));
        Assert.Equal(("Furto", false), ((string)furto["encerramento"]!, (bool)furto["aberto"]!));
        var baixado = await BemAtualAsync(bem);
        Assert.Equal(("Baixado", "Furto", "Furtada no acampamento (BO 123)"), ((string)baixado["situacao"]!, (string)baixado["baixa"]!["motivo"]!,
            (string)baixado["baixa"]!["justificativa"]!));
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Emprestimos/{emprestimo}/PerdaFurto",
            new { data = D(Hoje), motivo = "Perda", justificativa = "x" }), HttpStatusCode.Conflict, "já encerrado");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Baixa",
            new { dataBaixa = D(Hoje), motivo = "Descarte", justificativa = "x" }), HttpStatusCode.Conflict, "Bem baixado");

        var tipos = (await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Bens/{bem}/Historico")))["items"]!.AsArray()
            .Select(h => (string)h!["tipo"]!).Distinct().ToList();
        Assert.Equal(["Cadastro", "EntradaManutencao", "SaidaManutencao", "Emprestimo", "PerdaOuFurto"], tipos);

        var outro = Id(await BemAsync(categoria));
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{outro}/Manutencoes", new { dataEntrada = D(Hoje.AddDays(-2)), motivo = "Irreparável?" }),
            HttpStatusCode.Created);
        await Status(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Bens/{outro}")
        {
            Content = JsonContent.Create(new { justificativa = "tentativa", versao = Versao(await BemAtualAsync(outro)) }),
        }), HttpStatusCode.Conflict, "já teve movimentações");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{outro}/Baixa",
            new { dataBaixa = D(Hoje.AddDays(-3)), motivo = "Descarte", justificativa = "x" }), HttpStatusCode.BadRequest, "última movimentação");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{outro}/Baixa", new { dataBaixa = D(Hoje), justificativa = "x" }),
            HttpStatusCode.BadRequest, "motivo é obrigatório");
        var descarte = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{outro}/Baixa",
            new { dataBaixa = D(Hoje), motivo = "Descarte", justificativa = "Sem conserto" }));
        Assert.Equal(("Baixado", "Descarte"), ((string)descarte["situacao"]!, (string)descarte["baixa"]!["motivo"]!));
        var manutencoes = (await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Bens/{outro}/Manutencoes")))["items"]!.AsArray();
        Assert.Equal((false, D(Hoje)), ((bool)manutencoes[0]!["aberta"]!, (string)manutencoes[0]!["dataSaida"]!));
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Bens/{Guid.NewGuid()}/Manutencoes"), HttpStatusCode.NotFound);
        var corpo = BemCorpo(categoria);
        corpo["versao"] = Versao(descarte);
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{outro}", corpo), HttpStatusCode.Conflict, "Bem baixado");

        var perdido = Id(await BemAsync(categoria));
        var emprestado = Id(await Esperar(await EmprestarAsync(perdido, membro), HttpStatusCode.Created));
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Emprestimos/{emprestado}/PerdaFurto",
            new { data = D(Hoje), motivo = "Perda", justificativa = "Perdida na trilha" }));
        Assert.Equal("Perda", (string)(await BemAtualAsync(perdido))["baixa"]!["motivo"]!);
    }

    [Fact]
    public async Task CA07_HistoricoERelatorios_ExportamCsvPatrimonialEDeEmprestimos()
    {
        var categoria = Categoria();
        var membro = await MembroAsync();
        var formula = BemCorpo(categoria, "=HYPERLINK(\"x\")", valor: 1234.5m);
        formula["descricao"] = "Linha; com \"aspas\"";
        var bem = Id(await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", formula), HttpStatusCode.Created));
        var livro = BemCorpo(categoria, "Livro de atas", comValor: false);
        livro["dataAquisicao"] = null;
        livro["dataAquisicaoDesconhecida"] = true;
        livro["justificativaDataDesconhecida"] = "Acervo antigo";
        await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", livro), HttpStatusCode.Created);
        await Esperar(await EmprestarAsync(bem, membro, Hoje.AddDays(-9), Hoje.AddDays(-2)), HttpStatusCode.Created);

        var csv = await Admin.GetAsync($"/api/Almoxarifado/Relatorios/Patrimonio/Csv?categoria={categoria}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("patrimonio-", csv.Content.Headers.ContentDisposition!.FileNameStar ?? csv.Content.Headers.ContentDisposition.FileName);
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var linhas = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, linhas.Length);
        Assert.StartsWith("Tombo;Nome;Descrição;Categoria;Conservação;Localização;Situação", linhas[0]);
        Assert.Contains("'=HYPERLINK(\"\"x\"\")", linhas[1]);
        Assert.Contains("\"Linha; com \"\"aspas\"\"\"", linhas[1]);
        Assert.Contains(";Emprestado;", linhas[1]);
        Assert.Contains(";1234,50;", linhas[1]);
        Assert.Contains(";Desconhecida;", linhas[2]);
        Assert.Contains(";Não;", linhas[2]);

        var emprestimosCsv = await Admin.GetAsync("/api/Almoxarifado/Relatorios/Emprestimos/Csv?atrasados=true");
        var texto = Encoding.UTF8.GetString((await emprestimosCsv.Content.ReadAsByteArrayAsync())[3..]);
        Assert.StartsWith("Tombo;Bem;Membro;Data de retirada;Previsão de devolução", texto);
        Assert.Contains(";Sim;", texto);
        Assert.Contains("emprestimos-atrasados-", emprestimosCsv.Content.Headers.ContentDisposition!.FileNameStar ?? "");
        Assert.Contains("emprestimos-abertos-",
            (await Admin.GetAsync("/api/Almoxarifado/Relatorios/Emprestimos/Csv")).Content.Headers.ContentDisposition!.FileNameStar ?? "");
        Assert.Contains((await Esperar(await Admin.GetAsync("/api/Almoxarifado/Relatorios/Emprestimos")))["items"]!.AsArray(),
            e => Guid.Parse((string)e!["bem"]!["id"]!) == bem);

        var relatorio = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Relatorios/Patrimonio?categoria={categoria}"));
        var porSituacao = relatorio["porSituacao"]!.AsArray().ToDictionary(s => (string)s!["situacao"]!, s => (int)s!["quantidade"]!);
        Assert.Equal(new Dictionary<string, int> { ["Disponivel"] = 1, ["Emprestado"] = 1, ["EmManutencao"] = 0, ["Baixado"] = 0 }, porSituacao);
        Assert.Equal(1234.5m, (decimal)relatorio["valorEstimadoPatrimonial"]!);
        await Status(await Admin.GetAsync("/api/Almoxarifado/Relatorios/Patrimonio/Csv?situacao=99"), HttpStatusCode.BadRequest);

        var enums = await Esperar(await Admin.GetAsync("/api/Almoxarifado/Enums"));
        Assert.Equal(["Novo", "Bom", "Regular", "Ruim", "Inservível"], enums["estadoConservacao"]!.AsArray().Select(e => (string)e!["descricao"]!));
        Assert.Equal(19, enums["tipoMovimentacaoAlmoxarifado"]!.AsArray().Count);
    }

    [Fact]
    public async Task CA08_InventarioFechadoEImutavel_VotacaoRegistradaDepoisSemSimularAprovacao()
    {
        var categoria = Categoria();
        var responsavel = await MembroAsync("SEC");
        var bem = await BemAsync(categoria, "Bandeira do clube");
        var baixado = Id(await BemAsync(categoria, "Mastro quebrado"));
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{baixado}/Baixa",
            new { dataBaixa = D(Hoje), motivo = "Descarte", justificativa = "Quebrado" }));

        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Inventarios", new { dataInventario = D(Hoje.AddDays(3)), responsavelId = responsavel }),
            HttpStatusCode.BadRequest, "não pode ser futura");
        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Inventarios", new { dataInventario = D(Hoje) }), HttpStatusCode.BadRequest,
            "responsavelId é obrigatório");
        var fechado = await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Inventarios",
            new { dataInventario = D(Hoje), responsavelId = responsavel, observacoes = "Conferência anual" }), HttpStatusCode.Created);
        var inventario = fechado["inventario"]!;
        Assert.Equal((false, (JsonNode?)null), ((bool)inventario["votado"]!, inventario["votacao"]));
        var itens = fechado["itens"]!.AsArray();
        Assert.Contains(itens, i => Guid.Parse((string)i!["bemId"]!) == Id(bem));
        Assert.DoesNotContain(itens, i => Guid.Parse((string)i!["bemId"]!) == baixado);
        Assert.Equal(itens.Count, (int)inventario["totalBens"]!);
        var id = Id(inventario);

        var corpo = BemCorpo(categoria, "Bandeira oficial do clube", valor: 999m);
        corpo["versao"] = Versao(bem);
        await Esperar(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{Id(bem)}", corpo));
        var depois = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Inventarios/{id}"));
        var item = depois["itens"]!.AsArray().Single(i => Guid.Parse((string)i!["bemId"]!) == Id(bem))!;
        Assert.Equal(("Bandeira do clube", 150.50m), ((string)item["nome"]!, (decimal)item["valorEstimado"]!));

        var erroItem = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync("""
            DECLARE @u uniqueidentifier = (SELECT Id FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV');
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.patrimonio_inventario_itens SET Nome = 'adulterado' WHERE InventarioId = @id;
            """, ("@id", id)));
        Assert.Equal(50131, erroItem.Number);
        var erroCabecalho = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync("""
            DECLARE @u uniqueidentifier = (SELECT Id FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV');
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.patrimonio_inventarios SET TotalBens = TotalBens + 1 WHERE Id = @id;
            """, ("@id", id)));
        Assert.Equal(50132, erroCabecalho.Number);

        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Inventarios/{id}/Votacao",
            new { dataVotacao = D(Hoje.AddDays(-1)), referenciaAta = "Ata 1", versao = Versao(inventario) }), HttpStatusCode.BadRequest, "anterior ao inventário");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Inventarios/{id}/Votacao",
            new { dataVotacao = D(Hoje), versao = Versao(inventario) }), HttpStatusCode.BadRequest, "referenciaAta é obrigatório");
        var votado = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Inventarios/{id}/Votacao",
            new { dataVotacao = D(Hoje), referenciaAta = "Ata 12/2026, item 4", versao = Versao(inventario) }));
        Assert.Equal((true, "Ata 12/2026, item 4"), ((bool)votado["inventario"]!["votado"]!, (string)votado["inventario"]!["votacao"]!["referenciaAta"]!));
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Inventarios/{id}/Votacao",
            new { dataVotacao = D(Hoje), referenciaAta = "Outra", versao = Versao(votado["inventario"]!) }), HttpStatusCode.Conflict, "já registrada");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Inventarios/{id}/Votacao",
            new { dataVotacao = D(Hoje), referenciaAta = "Outra", versao = Versao(inventario) }), HttpStatusCode.Conflict, "Versão desatualizada");
        var erroVotado = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync("""
            DECLARE @u uniqueidentifier = (SELECT Id FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV');
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.patrimonio_inventarios SET ReferenciaAta = 'troca' WHERE Id = @id;
            """, ("@id", id)));
        Assert.Equal(50132, erroVotado.Number);
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Inventarios/{Guid.NewGuid()}/Votacao",
            new { dataVotacao = D(Hoje), referenciaAta = "x", versao = Versao(inventario) }), HttpStatusCode.NotFound);

        var lista = await Esperar(await Admin.GetAsync("/api/Almoxarifado/Inventarios?pageSize=100"));
        Assert.Contains(lista["items"]!.AsArray(), i => Id(i!) == id && (bool)i!["votado"]!);
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Inventarios/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        await Status(await Admin.GetAsync("/api/Almoxarifado/Inventarios?page=0"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CA09_CrudDaListaDeDesejos_TresUrgencias_Description_FiltrosEOrdenacao()
    {
        var categoria = Categoria();
        var pouco = await DesejoAsync(categoria, "Lanterna", 2, "PoucoUrgenteMasNecessario");
        var muito = await DesejoAsync(categoria, "Kit primeiros socorros", 1, "MuitoUrgente", custo: null);
        var urgente1 = await DesejoAsync(categoria, "Corda 20m", 3, "Urgente");
        var urgente2 = await DesejoAsync(categoria, "Corda 50m", 1, 2);
        Assert.Equal(("Pouco urgente, mas necessário", "Muito urgente", "Urgente"), ((string)pouco["urgenciaDescricao"]!,
            (string)muito["urgenciaDescricao"]!, (string)urgente2["urgenciaDescricao"]!));
        Assert.Equal(("Pendente", 0, 3, 1140m), ((string)urgente1["situacao"]!, (int)urgente1["quantidadeAtendida"]!, (int)urgente1["quantidadePendente"]!,
            (decimal)urgente1["custoTotalEstimado"]!));
        Assert.Null(muito["custoTotalEstimado"]);
        Assert.Equal("Administrador", (string)pouco["solicitante"]!["nome"]!);

        var cancelado = await DesejoAsync(categoria, "Apito", 1, "MuitoUrgente");
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(cancelado)}/Cancelar",
            new { justificativa = "Comprado pela unidade", versao = Versao(cancelado) }));

        var ordem = (await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}"))).Select(d => (string)d!["nome"]!).ToList();
        Assert.Equal(["Kit primeiros socorros", "Corda 20m", "Corda 50m", "Lanterna", "Apito"], ordem);
        Assert.Equal(["Corda 20m", "Corda 50m"], (await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}&urgencia=Urgente")))
            .Select(d => (string)d!["nome"]!));
        Assert.Single(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}&situacao=Cancelada")));
        Assert.Equal(4, (await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}&somenteAbertas=true"))).Count);
        Assert.Equal(["Corda 20m", "Corda 50m"], (await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}&busca=Corda")))
            .Select(d => (string)d!["nome"]!));
        Assert.Empty(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}&tipoItem=Consumivel")));

        var corpo = DesejoCorpo(categoria, "Lanterna LED", 4, "Urgente");
        corpo["versao"] = Versao(pouco);
        var atualizado = await Esperar(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(pouco)}", corpo));
        Assert.Equal(("Lanterna LED", 4, "Urgente"), ((string)atualizado["nome"]!, (int)atualizado["quantidadeDesejada"]!, (string)atualizado["urgencia"]!));
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(pouco)}", corpo), HttpStatusCode.Conflict, "Versão desatualizada");
        var obtido = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{Id(pouco)}"));
        Assert.Equal(Versao(atualizado), Versao(obtido));
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{Guid.NewGuid()}"), HttpStatusCode.NotFound);

        var excluido = await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Desejos/{Id(urgente2)}")
        {
            Content = JsonContent.Create(new { justificativa = "Cadastro duplicado", versao = Versao(urgente2) }),
        }));
        Assert.True((bool)excluido["excluido"]!);
        Assert.DoesNotContain(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}")), d => Id(d!) == Id(urgente2));
        Assert.Contains(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Desejos?categoria={categoria}&incluirExcluidos=true")), d => Id(d!) == Id(urgente2));
        var historico = (await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{Id(pouco)}/Historico")))["items"]!.AsArray();
        Assert.Equal(["CadastroDesejo", "AlteracaoDesejo"], historico.Select(h => (string)h!["tipo"]!));
        Assert.Equal(2, (int)historico[1]!["estadoAnterior"]!["QuantidadeDesejada"]!);
        await Status(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{Guid.NewGuid()}/Historico"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CA10_CA11_AtendimentoParcialEFinal_VinculaBensSemDuplicar_EDesejoIsoladoNaoMexeEmPatrimonioNemFinanceiro()
    {
        var categoria = Categoria();
        var recebedor = await MembroAsync("SEC");
        var bensAntes = await Db(db => db.BensPatrimoniais.CountAsync());
        var lancamentosAntes = await Db(db => db.Lancamentos.CountAsync());
        var desejo = await DesejoAsync(categoria, "Barraca 4 pessoas", 5);
        Assert.Equal((bensAntes, lancamentosAntes), (await Db(db => db.BensPatrimoniais.CountAsync()), await Db(db => db.Lancamentos.CountAsync())));
        var id = Id(desejo);

        var parcial = await Esperar(await AtenderAsync(id, new
        {
            quantidade = 2, data = D(Hoje), responsavelId = recebedor, origem = "Compra",
            bensNovos = new[] { NovoBem("Barraca A", categoria), NovoBem("Barraca B", categoria) },
        }), HttpStatusCode.Created);
        Assert.True((bool)parcial["criado"]!);
        Assert.Equal(("ParcialmenteAtendida", 2, 3, 1140m), ((string)parcial["desejo"]!["situacao"]!, (int)parcial["desejo"]!["quantidadeAtendida"]!,
            (int)parcial["desejo"]!["quantidadePendente"]!, (decimal)parcial["desejo"]!["custoPendenteEstimado"]!));
        var criados = parcial["atendimento"]!["bens"]!.AsArray();
        Assert.Equal(2, criados.Count);
        Assert.All(criados, b => Assert.True((bool)b!["criadoPeloAtendimento"]!));
        var barraca = await BemAtualAsync(Guid.Parse((string)criados[0]!["bemId"]!));
        Assert.Equal(("Compra", D(Hoje), id), ((string)barraca["origem"]!, (string)barraca["dataAquisicao"]!, Guid.Parse((string)barraca["origemDesejo"]!["desejoId"]!)));
        Assert.Equal(lancamentosAntes, await Db(db => db.Lancamentos.CountAsync()));

        var existente = Id(await BemAsync(categoria, "Barraca doada antes"));
        await Status(await AtenderAsync(id, new { quantidade = 4, data = D(Hoje), responsavelId = recebedor, origem = "Doacao" }), HttpStatusCode.Conflict,
            "Quantidade superior à pendente");
        await Status(await AtenderAsync(id, new { quantidade = 2, data = D(Hoje), responsavelId = recebedor, origem = "Doacao", bensExistentes = new[] { existente } }),
            HttpStatusCode.BadRequest, "informe exatamente 2");
        await Status(await AtenderAsync(id, new
        {
            quantidade = 1, data = D(Hoje), responsavelId = recebedor, origem = "Doacao", bensExistentes = new[] { Guid.Parse((string)criados[0]!["bemId"]!) },
        }), HttpStatusCode.Conflict, "já vinculado");
        await Status(await AtenderAsync(id, new { quantidade = 1, data = D(Hoje), responsavelId = recebedor, origem = "Doacao", bensExistentes = new[] { Guid.NewGuid() } }),
            HttpStatusCode.NotFound);

        var final = await Esperar(await AtenderAsync(id, new
        {
            quantidade = 3, data = D(Hoje), responsavelId = recebedor, origem = "Doacao", observacoes = "Doação da família Silva",
            bensNovos = new[] { NovoBem("Barraca C", categoria), NovoBem("Barraca D", categoria) }, bensExistentes = new[] { existente },
        }), HttpStatusCode.Created);
        Assert.Equal(("Atendida", 5, 0), ((string)final["desejo"]!["situacao"]!, (int)final["desejo"]!["quantidadeAtendida"]!,
            (int)final["desejo"]!["quantidadePendente"]!));
        Assert.Contains(final["atendimento"]!["bens"]!.AsArray(), b => Guid.Parse((string)b!["bemId"]!) == existente && !(bool)b!["criadoPeloAtendimento"]!);
        Assert.Equal("Doacao", (string)(await BemAtualAsync(Guid.Parse((string)final["atendimento"]!["bens"]!.AsArray()
            .First(b => (bool)b!["criadoPeloAtendimento"]!)!["bemId"]!)))["origem"]!);
        Assert.Equal(bensAntes + 5, await Db(db => db.BensPatrimoniais.CountAsync()));
        Assert.Equal(lancamentosAntes, await Db(db => db.Lancamentos.CountAsync()));
        await Status(await AtenderAsync(id, new { quantidade = 1, data = D(Hoje), responsavelId = recebedor, origem = "Compra", bensNovos = new[] { NovoBem("x", categoria) } }),
            HttpStatusCode.Conflict, "já atendida");
        var detalhe = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{id}"));
        Assert.Equal(2, detalhe["atendimentos"]!.AsArray().Count);
        Assert.Equal(5, detalhe["atendimentos"]!.AsArray().Sum(a => a!["bens"]!.AsArray().Count));

        var consumivel = Id(await DesejoAsync(categoria, "Fita crepe", 10, "PoucoUrgenteMasNecessario", "Consumivel", 5m));
        await Status(await AtenderAsync(consumivel, new
        {
            quantidade = 1, data = D(Hoje), responsavelId = recebedor, origem = "Compra", bensNovos = new[] { NovoBem("Fita", categoria) },
        }), HttpStatusCode.BadRequest, "não gera patrimônio");
        var fita = await Esperar(await AtenderAsync(consumivel, new { quantidade = 4, data = D(Hoje), responsavelId = recebedor, origem = "Compra" }),
            HttpStatusCode.Created);
        Assert.Equal(("ParcialmenteAtendida", 30m), ((string)fita["desejo"]!["situacao"]!, (decimal)fita["desejo"]!["custoPendenteEstimado"]!));
        Assert.Empty(fita["atendimento"]!["bens"]!.AsArray());
        Assert.Equal(bensAntes + 5, await Db(db => db.BensPatrimoniais.CountAsync()));

        var relatorio = (await Esperar(await Admin.GetAsync("/api/Almoxarifado/Relatorios/Desejos")))["items"]!.AsArray();
        Assert.Equal(["MuitoUrgente", "Urgente", "PoucoUrgenteMasNecessario"], relatorio.Select(r => (string)r!["urgencia"]!));
        Assert.True((int)relatorio[2]!["quantidadePendente"]! >= 6);
        Assert.True((decimal)relatorio[2]!["custoPendenteEstimado"]! >= 30m);

        var corpo = DesejoCorpo(categoria, "Barraca 4 pessoas", 4);
        corpo["versao"] = Versao(final["desejo"]!);
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Desejos/{id}", corpo), HttpStatusCode.Conflict, "menor que a atendida");
        corpo["quantidadeDesejada"] = 7;
        var aumentado = await Esperar(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Desejos/{id}", corpo));
        Assert.Equal(("ParcialmenteAtendida", 2), ((string)aumentado["situacao"]!, (int)aumentado["quantidadePendente"]!));
        corpo["versao"] = Versao(aumentado);
        corpo["tipoItem"] = "Consumivel";
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Desejos/{id}", corpo), HttpStatusCode.Conflict, "Tipo do item não pode mudar");
    }

    [Fact]
    public async Task CA12_RejeitaEnumInvalido_QuantidadesInconsistentes_ValoresNegativosEOperacoesIncompativeis()
    {
        var categoria = Categoria();
        foreach (var (campo, valor, mensagem) in new (string, object?, string)[]
        {
            ("urgencia", 9, "urgencia deve ser um destes valores"),
            ("urgencia", 0, "urgencia deve ser um destes valores"),
            ("urgencia", null, "urgencia é obrigatório"),
            ("tipoItem", 3, "tipoItem deve ser um destes valores"),
            ("quantidadeDesejada", 0, "quantidadeDesejada deve ser um inteiro entre 1"),
            ("quantidadeDesejada", -2, "quantidadeDesejada deve ser um inteiro entre 1"),
            ("custoUnitarioEstimado", -5m, "custoUnitarioEstimado deve ser maior ou igual a zero"),
            ("linkReferencia", "ftp://exemplo.com", "linkReferencia deve ser uma URL http ou https"),
            ("justificativa", "", "justificativa é obrigatório"),
        })
        {
            var corpo = DesejoCorpo(categoria);
            corpo[campo] = valor;
            await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Desejos", corpo), HttpStatusCode.BadRequest, mensagem);
        }

        var nomeInvalido = DesejoCorpo(categoria);
        nomeInvalido["urgencia"] = "Urgentissimo";
        await Status(await Admin.PostAsJsonAsync("/api/Almoxarifado/Desejos", nomeInvalido), HttpStatusCode.BadRequest);
        await Status(await Admin.GetAsync("/api/Almoxarifado/Desejos?urgencia=4"), HttpStatusCode.BadRequest, "urgencia");
        await Status(await Admin.GetAsync("/api/Almoxarifado/Bens/00000000-0000-0000-0000-000000000000/Historico?tipo=99"), HttpStatusCode.BadRequest);

        var desejo = await DesejoAsync(categoria, quantidade: 2, tipo: "Consumivel");
        var membro = await MembroAsync();
        var semChave = new HttpRequestMessage(HttpMethod.Post, $"/api/Almoxarifado/Desejos/{Id(desejo)}/Atendimentos")
        {
            Content = JsonContent.Create(new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Compra" }),
        };
        await Status(await Admin.SendAsync(semChave), HttpStatusCode.BadRequest, "Idempotency-Key");
        foreach (var (corpo, mensagem) in new (object, string)[]
        {
            (new { quantidade = 0, data = D(Hoje), responsavelId = membro, origem = "Compra" }, "quantidade deve ser um inteiro entre 1"),
            (new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Outro" }, ""),
            (new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = 3 }, "origem deve ser um destes valores"),
            (new { quantidade = 1, data = D(Hoje), origem = "Compra" }, "responsavelId é obrigatório"),
            (new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Compra", bensExistentes = new[] { Guid.Empty } }, "GUID vazio"),
        })
        {
            await Status(await AtenderAsync(Id(desejo), corpo), HttpStatusCode.BadRequest, mensagem);
        }

        await Status(await AtenderAsync(Id(desejo), new { quantidade = 1, data = D(Hoje.AddDays(5)), responsavelId = membro, origem = "Compra" }),
            HttpStatusCode.BadRequest, "data não pode ser futura");
        await Status(await AtenderAsync(Id(desejo), new { quantidade = 3, data = D(Hoje), responsavelId = membro, origem = "Compra" }), HttpStatusCode.Conflict,
            "Restam 2");
        await Status(await AtenderAsync(Guid.NewGuid(), new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Compra" }), HttpStatusCode.NotFound);
        var inativo = await MembroAsync();
        await InativarAsync(inativo);
        await Status(await AtenderAsync(Id(desejo), new { quantidade = 1, data = D(Hoje), responsavelId = inativo, origem = "Compra" }), HttpStatusCode.BadRequest,
            "responsavelId deve ser um membro ativo");

        var cancelado = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(desejo)}/Cancelar",
            new { justificativa = "Não é mais necessário", versao = Versao(desejo) }));
        await Status(await AtenderAsync(Id(desejo), new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Compra" }), HttpStatusCode.Conflict,
            "Necessidade cancelada");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(desejo)}/Cancelar",
            new { justificativa = "de novo", versao = Versao(cancelado) }), HttpStatusCode.Conflict, "Necessidade cancelada");
        var corpoCancelado = DesejoCorpo(categoria);
        corpoCancelado["versao"] = Versao(cancelado);
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(desejo)}", corpoCancelado), HttpStatusCode.Conflict, "Necessidade cancelada");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(desejo)}/Cancelar", new { justificativa = "x" }), HttpStatusCode.BadRequest,
            "versao é obrigatória");
    }

    [Fact]
    public async Task CA13_CancelamentoEExclusaoLogica_PreservamAtendimentosEHistorico_EEstornoCorrigeAtendimentoIndevido()
    {
        var categoria = Categoria();
        var membro = await MembroAsync();
        var desejo = Id(await DesejoAsync(categoria, "Panela", 4));
        var primeiro = await Esperar(await AtenderAsync(desejo, new
        {
            quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Compra", bensNovos = new[] { NovoBem("Panela 1", categoria) },
        }), HttpStatusCode.Created);
        var existente = Id(await BemAsync(categoria, "Panela antiga"));
        var segundo = await Esperar(await AtenderAsync(desejo, new
        {
            quantidade = 2, data = D(Hoje), responsavelId = membro, origem = "Doacao", bensNovos = new[] { NovoBem("Panela 2", categoria) },
            bensExistentes = new[] { existente },
        }), HttpStatusCode.Created);
        var panela2 = Guid.Parse((string)segundo["atendimento"]!["bens"]!.AsArray().First(b => (bool)b!["criadoPeloAtendimento"]!)!["bemId"]!);
        var atendimento2 = Id(segundo["atendimento"]!);

        await Status(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Bens/{panela2}")
        {
            Content = JsonContent.Create(new { justificativa = "tentativa", versao = Versao(await BemAtualAsync(panela2)) }),
        }), HttpStatusCode.Conflict, "Estorne o atendimento");

        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{desejo}/Atendimentos/{atendimento2}/Estorno", new { justificativa = "" }),
            HttpStatusCode.BadRequest);
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{desejo}/Atendimentos/{atendimento2}/Estorno", new { }),
            HttpStatusCode.BadRequest, "justificativa é obrigatório");
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{desejo}/Atendimentos/{Guid.NewGuid()}/Estorno", new { justificativa = "x" }),
            HttpStatusCode.NotFound);
        var estornado = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{desejo}/Atendimentos/{atendimento2}/Estorno",
            new { justificativa = "Lançado no desejo errado" }));
        Assert.Equal(("ParcialmenteAtendida", 1), ((string)estornado["situacao"]!, (int)estornado["quantidadeAtendida"]!));
        var atendimentoEstornado = estornado["atendimentos"]!.AsArray().Single(a => Id(a!) == atendimento2)!;
        Assert.Equal((true, "Lançado no desejo errado"), ((bool)atendimentoEstornado["estornado"]!, (string)atendimentoEstornado["motivoEstorno"]!));
        Assert.All(atendimentoEstornado["bens"]!.AsArray(), b => Assert.False((bool)b!["vinculoAtivo"]!));
        Assert.True((bool)(await BemAtualAsync(panela2))["excluido"]!);
        var existenteDepois = await BemAtualAsync(existente);
        Assert.Equal((false, (JsonNode?)null), ((bool)existenteDepois["excluido"]!, existenteDepois["origemDesejo"]));
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{desejo}/Atendimentos/{atendimento2}/Estorno", new { justificativa = "x" }),
            HttpStatusCode.Conflict, "já estornado");

        var panela1 = Guid.Parse((string)primeiro["atendimento"]!["bens"]![0]!["bemId"]!);
        await Esperar(await EmprestarAsync(panela1, membro, Hoje, Hoje.AddDays(2)), HttpStatusCode.Created);
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{desejo}/Atendimentos/{Id(primeiro["atendimento"]!)}/Estorno",
            new { justificativa = "x" }), HttpStatusCode.Conflict, "já foram movimentados");

        var reaproveitado = await Esperar(await AtenderAsync(desejo, new
        {
            quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Doacao", bensExistentes = new[] { existente },
        }), HttpStatusCode.Created);
        Assert.Equal(2, (int)reaproveitado["desejo"]!["quantidadeAtendida"]!);

        var atual = await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{desejo}"));
        var cancelado = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{desejo}/Cancelar",
            new { justificativa = "Restante comprado pela unidade", versao = Versao(atual) }));
        Assert.Equal(("Cancelada", 2, 0, "Restante comprado pela unidade"), ((string)cancelado["situacao"]!, (int)cancelado["quantidadeAtendida"]!,
            (int)cancelado["quantidadePendente"]!, (string)cancelado["motivoCancelamento"]!));
        Assert.Equal(3, cancelado["atendimentos"]!.AsArray().Count);
        var excluido = await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Desejos/{desejo}")
        {
            Content = JsonContent.Create(new { justificativa = "Encerrado", versao = Versao(cancelado) }),
        }));
        Assert.Equal((true, 3), ((bool)excluido["excluido"]!, excluido["atendimentos"]!.AsArray().Count));
        await Status(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Desejos/{desejo}")
        {
            Content = JsonContent.Create(new { justificativa = "de novo", versao = Versao(excluido) }),
        }), HttpStatusCode.Conflict, "já excluída");

        var tipos = (await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{desejo}/Historico")))["items"]!.AsArray()
            .Select(h => (string)h!["tipo"]!).Distinct().ToList();
        Assert.Equal(["CadastroDesejo", "Atendimento", "EstornoAtendimento", "CancelamentoDesejo", "ExclusaoDesejo"], tipos);
        Assert.True(await ScalarAsync("SELECT COUNT(*) FROM dbo.almoxarifado_historico WHERE DesejoId = @d AND Tabela = 'almoxarifado_atendimentos_bens'",
            ("@d", desejo)) >= 4);

        var atendido = await DesejoAsync(categoria, "Mapa", 1, tipo: "Consumivel");
        var completo = await Esperar(await AtenderAsync(Id(atendido), new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Compra" }),
            HttpStatusCode.Created);
        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Desejos/{Id(atendido)}/Cancelar",
            new { justificativa = "x", versao = Versao(completo["desejo"]!) }), HttpStatusCode.Conflict, "já atendida");
    }

    [Fact]
    public async Task CA14_AutorizacaoNoBackend_PorCargoEPorDesignacao()
    {
        var categoria = Categoria();
        var bem = Id(await BemAsync(categoria));
        var desejo = Id(await DesejoAsync(categoria));
        using var anonimo = fx.Factory.CreateClient();
        await Status(await anonimo.GetAsync("/api/Almoxarifado/Bens"), HttpStatusCode.Unauthorized);
        await Status(await anonimo.PostAsJsonAsync("/api/Almoxarifado/Bens", BemCorpo(categoria)), HttpStatusCode.Unauthorized);

        var (ds, dsId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, "DS");
        foreach (var rota in new[] { "/api/Almoxarifado/Bens", $"/api/Almoxarifado/Bens/{bem}", "/api/Almoxarifado/Desejos", "/api/Almoxarifado/Relatorios/Patrimonio",
                     "/api/Almoxarifado/Inventarios", "/api/Almoxarifado/Emprestimos", "/api/Almoxarifado/Enums", "/api/Almoxarifado/Responsaveis" })
        {
            await Status(await ds.GetAsync(rota), HttpStatusCode.Forbidden);
        }

        foreach (var role in new[] { "TES", "CONS", "INST", "TESU", "CAP" })
        {
            var (consulta, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, role);
            await Status(await consulta.GetAsync($"/api/Almoxarifado/Bens?categoria={categoria}"), HttpStatusCode.OK);
            await Status(await consulta.GetAsync($"/api/Almoxarifado/Desejos/{desejo}"), HttpStatusCode.OK);
            await Status(await consulta.GetAsync("/api/Almoxarifado/Relatorios/Patrimonio/Csv"), HttpStatusCode.OK);
            await Status(await consulta.PostAsJsonAsync("/api/Almoxarifado/Bens", BemCorpo(categoria)), HttpStatusCode.Forbidden);
            await Status(await EmprestarAsync(bem, dsId, client: consulta), HttpStatusCode.Forbidden);
            await Status(await AtenderAsync(desejo, new { quantidade = 1 }, client: consulta), HttpStatusCode.Forbidden);
            await Status(await consulta.PostAsJsonAsync("/api/Almoxarifado/Inventarios", new { dataInventario = D(Hoje) }), HttpStatusCode.Forbidden);
            await Status(await consulta.PostAsJsonAsync("/api/Almoxarifado/Responsaveis", new { membroId = dsId, dataInicio = D(Hoje) }), HttpStatusCode.Forbidden);
            consulta.Dispose();
        }

        foreach (var role in new[] { "ALM", "SEC", "DIR", "DIRA" })
        {
            var (gestao, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, role);
            var criado = await Esperar(await gestao.PostAsJsonAsync("/api/Almoxarifado/Bens", BemCorpo(categoria, $"Bem do {role}")), HttpStatusCode.Created);
            Assert.Equal($"Usuário Teste {role}", (string)criado["criacao"]!["por"]!["nome"]!);
            await Esperar(await gestao.PostAsJsonAsync("/api/Almoxarifado/Desejos", DesejoCorpo(categoria, $"Desejo do {role}")), HttpStatusCode.Created);
            gestao.Dispose();
        }

        var (almoxarife, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, "ALM");
        await Status(await almoxarife.PostAsJsonAsync("/api/Almoxarifado/Responsaveis", new { membroId = dsId, dataInicio = D(Hoje) }), HttpStatusCode.Forbidden);

        var (conselheiro, conselheiroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, "CONS");
        await Status(await conselheiro.PostAsJsonAsync("/api/Almoxarifado/Bens", BemCorpo(categoria)), HttpStatusCode.Forbidden);
        var (secretario, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, "SEC");
        await Status(await secretario.PostAsJsonAsync("/api/Almoxarifado/Responsaveis", new { membroId = conselheiroId, dataInicio = D(Hoje.AddDays(3)) }),
            HttpStatusCode.BadRequest, "dataInicio não pode ser futura");
        var designacao = await Esperar(await secretario.PostAsJsonAsync("/api/Almoxarifado/Responsaveis",
            new { membroId = conselheiroId, dataInicio = D(Hoje), observacoes = "Ata 10/2026" }), HttpStatusCode.Created);
        Assert.Equal((true, "Conselheiro"), ((bool)designacao["vigente"]!, (string)designacao["cargoMembro"]!));
        await Status(await secretario.PostAsJsonAsync("/api/Almoxarifado/Responsaveis", new { membroId = conselheiroId, dataInicio = D(Hoje) }),
            HttpStatusCode.Conflict, "já designado");
        await Esperar(await conselheiro.PostAsJsonAsync("/api/Almoxarifado/Bens", BemCorpo(categoria, "Bem do designado")), HttpStatusCode.Created);
        await Status(await conselheiro.PostAsJsonAsync("/api/Almoxarifado/Responsaveis", new { membroId = dsId, dataInicio = D(Hoje) }), HttpStatusCode.Forbidden);

        var (dsDesignado, dsDesignadoId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, "DS");
        await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Responsaveis", new { membroId = dsDesignadoId, dataInicio = D(Hoje) }), HttpStatusCode.Created);
        await Status(await dsDesignado.GetAsync("/api/Almoxarifado/Bens"), HttpStatusCode.OK);

        var responsaveis = (await Esperar(await conselheiro.GetAsync("/api/Almoxarifado/Responsaveis")))["items"]!.AsArray();
        Assert.Contains(responsaveis, r => Id(r!) == Id(designacao));
        await Status(await secretario.PostAsJsonAsync($"/api/Almoxarifado/Responsaveis/{Id(designacao)}/Revogar", new { motivo = "", versao = Versao(designacao) }),
            HttpStatusCode.BadRequest);
        var revogada = await Esperar(await secretario.PostAsJsonAsync($"/api/Almoxarifado/Responsaveis/{Id(designacao)}/Revogar",
            new { motivo = "Fim do mandato", versao = Versao(designacao) }));
        Assert.Equal((false, "Fim do mandato"), ((bool)revogada["vigente"]!, (string)revogada["motivoRevogacao"]!));
        await Status(await conselheiro.PostAsJsonAsync("/api/Almoxarifado/Bens", BemCorpo(categoria)), HttpStatusCode.Forbidden);
        await Status(await conselheiro.GetAsync("/api/Almoxarifado/Bens"), HttpStatusCode.OK);
        await Status(await secretario.PostAsJsonAsync($"/api/Almoxarifado/Responsaveis/{Id(designacao)}/Revogar",
            new { motivo = "de novo", versao = Versao(revogada) }), HttpStatusCode.Conflict, "já revogada");
        await Status(await secretario.PostAsJsonAsync($"/api/Almoxarifado/Responsaveis/{Guid.NewGuid()}/Revogar", new { motivo = "x", versao = Versao(revogada) }),
            HttpStatusCode.NotFound);
        Assert.DoesNotContain((await Esperar(await Admin.GetAsync("/api/Almoxarifado/Responsaveis")))["items"]!.AsArray(), r => Id(r!) == Id(designacao));
        Assert.Contains((await Esperar(await Admin.GetAsync("/api/Almoxarifado/Responsaveis?incluirRevogados=true")))["items"]!.AsArray(),
            r => Id(r!) == Id(designacao));
        var tipos = await ScalarAsync("SELECT COUNT(*) FROM dbo.almoxarifado_historico WHERE RegistroId = @id AND Acao IN ('Designacao', 'RevogacaoDesignacao')",
            ("@id", Id(designacao)));
        Assert.Equal(2, tipos);
        foreach (var cliente in new[] { ds, almoxarife, conselheiro, secretario, dsDesignado })
        {
            cliente.Dispose();
        }
    }

    [Fact]
    public async Task CA15_ConcorrenciaERepeticao_NaoDuplicamEmprestimosNemAtendimentos()
    {
        var categoria = Categoria();
        var membro = await MembroAsync();
        var bem = Id(await BemAsync(categoria));
        var emprestimos = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => EmprestarAsync(bem, membro)));
        Assert.Equal(1, emprestimos.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(emprestimos.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await Db(db => db.EmprestimosBens.CountAsync(e => e.BemId == bem)));

        var desejo = Id(await DesejoAsync(categoria, "Lampião", 3));
        var chave = Guid.NewGuid();
        object Corpo() => new
        {
            quantidade = 2, data = D(Hoje), responsavelId = membro, origem = "Compra",
            bensNovos = new[] { NovoBem("Lampião 1", categoria), NovoBem("Lampião 2", categoria) },
        };
        var repetidos = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AtenderAsync(desejo, Corpo(), chave)));
        Assert.Equal(1, repetidos.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(repetidos.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var replay = await Esperar(await AtenderAsync(desejo, Corpo(), chave));
        Assert.False((bool)replay["criado"]!);
        Assert.Equal(2, (int)replay["desejo"]!["quantidadeAtendida"]!);
        Assert.Equal(1, await Db(db => db.AtendimentosDesejos.CountAsync(a => a.DesejoId == desejo)));
        Assert.Equal(2, await Db(db => db.BensPatrimoniais.CountAsync(b => b.Categoria == categoria && b.Nome.StartsWith("Lampião"))));
        await Status(await AtenderAsync(desejo, new { quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Compra" }, chave),
            HttpStatusCode.Conflict, "dados diferentes");

        var concorrentes = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => AtenderAsync(desejo, new
        {
            quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Doacao", bensNovos = new[] { NovoBem($"Lampião extra {i}", categoria) },
        })));
        Assert.Equal(1, concorrentes.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(concorrentes.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(3, await Db(db => db.DesejosAlmoxarifado.Where(d => d.Id == desejo).Select(d => d.QuantidadeAtendida).SingleAsync()));
        Assert.Equal(3, await Db(db => db.BensPatrimoniais.CountAsync(b => b.Categoria == categoria && b.Nome.StartsWith("Lampião"))));

        var livre = Id(await BemAsync(categoria, "Bem disputado"));
        var outro = Id(await DesejoAsync(categoria, "Outro", 1));
        var terceiro = Id(await DesejoAsync(categoria, "Terceiro", 1));
        var disputa = await Task.WhenAll(new[] { outro, terceiro }.Select(d => AtenderAsync(d, new
        {
            quantidade = 1, data = D(Hoje), responsavelId = membro, origem = "Doacao", bensExistentes = new[] { livre },
        })));
        Assert.Equal(1, disputa.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, await Db(db => db.AtendimentosBens.CountAsync(v => v.BemId == livre && v.Ativo)));
    }

    [Fact]
    public async Task Migration_CargoAlmoxarifeExisteETabelaDeAuditoriaSoAceitaTrigger()
    {
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM dbo.Cargos WHERE Role = 'ALM'"));
        var erro = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(
            "INSERT INTO dbo.almoxarifado_desejos (Id, Nome, Descricao, Justificativa, TipoItem, QuantidadeDesejada, QuantidadeAtendida, Urgencia, Situacao, Excluido, CriadoPorUsuarioId, CriadoEmUtc) " +
            "SELECT NEWID(), 'x', 'x', 'x', 1, 1, 0, 1, 1, 0, Id, SYSUTCDATETIME() FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV'"));
        Assert.Equal(50130, erro.Number);
        var situacao = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync("""
            DECLARE @u uniqueidentifier = (SELECT Id FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV');
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@u;
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            INSERT INTO dbo.almoxarifado_desejos (Id, Nome, Descricao, Justificativa, TipoItem, QuantidadeDesejada, QuantidadeAtendida, Urgencia, Situacao, Excluido, CriadoPorUsuarioId, CriadoEmUtc)
            SELECT NEWID(), 'x', 'x', 'x', 1, 2, 1, 1, 1, 0, Id, SYSUTCDATETIME() FROM dbo.Usuarios WHERE EmailNormalizado = 'ADMIN@LOCAL.DEV';
            """));
        Assert.Contains("CK_almoxarifado_desejos_Situacao", situacao.Message);
    }
}
