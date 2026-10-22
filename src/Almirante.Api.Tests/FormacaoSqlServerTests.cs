using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

public sealed class FormacaoSqlFixture : IAsyncLifetime
{
    public const string Ip = "203.0.113.125";

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
/// Issue #125 contra SQL Server real: catálogo de especialidades, revisões de requisitos, vínculos membro–classe/especialidade, progresso
/// com grupos e evidências, conclusão, investidura/entrega, cancelamento, reabertura, liderança, registros históricos, isolamento por
/// participante e auditoria por trigger.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class FormacaoSqlServerTests(FormacaoSqlFixture fx) : IClassFixture<FormacaoSqlFixture>
{
    private static readonly DateOnly Hoje = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    private HttpClient Admin => fx.Admin;

    private Task<T> Db<T>(Func<AlmiranteDbContext, Task<T>> consulta) => TestHelpers.WithDbAsync(fx.Factory, consulta);

    private static string D(DateOnly data) => data.ToString("yyyy-MM-dd");

    private static async Task<JsonObject> Esperar(HttpResponseMessage resposta, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.True(resposta.StatusCode == esperado, $"esperado {(int)esperado}, obtido {(int)resposta.StatusCode}: {corpo}");
        return corpo.Length == 0 ? [] : JsonNode.Parse(corpo) as JsonObject ?? new JsonObject { ["items"] = JsonNode.Parse(corpo) };
    }

    private static Guid Id(JsonNode node) => Guid.Parse((string)node["id"]!);

    private static string Versao(JsonNode node) => (string)node["versao"]!;

    /// <summary>Id do requisito com o código, procurando na árvore requisitos/subitens.</summary>
    private static Guid Req(JsonNode node, string codigo)
    {
        IEnumerable<JsonNode> Nos(JsonNode? lista) => lista?.AsArray().SelectMany(n => new[] { n! }.Concat(Nos(n!["subitens"]))) ?? [];
        return Id(Nos(node["requisitos"]).Single(n => (string)n["codigo"]! == codigo));
    }

    private async Task<(HttpClient Client, Guid Id)> MembroAsync(string role = "DS", DateOnly? nascimento = null)
    {
        var (client, id) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(fx.Factory, role);
        if (nascimento is { } data)
        {
            await NascimentoAsync(id, data);
        }

        return (client, id);
    }

    /// <summary>O trigger de _usuarios_hist exige o contexto de auditoria também para a data de nascimento.</summary>
    private Task<int> NascimentoAsync(Guid membroId, DateOnly data) => Db(db => db.Database.ExecuteSqlInterpolatedAsync($"""
        EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={membroId};
        EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
        UPDATE dbo.Usuarios SET DataNascimento = {data.ToDateTime(TimeOnly.MinValue)} WHERE Id = {membroId};
        EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=NULL;
        EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value=NULL;
        """));

    private async Task<JsonObject> EspecialidadeAsync(string area = "Estudos da Natureza", string? codigo = null, string? nome = null,
        params Guid[] preRequisitos) =>
        await Esperar(await Admin.PostAsJsonAsync("/api/Especialidades", new
        {
            nome = nome ?? $"Especialidade {Guid.NewGuid():N}"[..40], codigoOficial = codigo, area, fonteOficial = "Manual de especialidades (teste)",
            edicaoRevisao = "2024", nivelDificuldade = 1, modalidade = "Basica", preRequisitos,
        }), HttpStatusCode.Created);

    private async Task<JsonObject> PublicarAsync(object alvo, params object[] requisitos)
    {
        var corpo = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(alvo, System.Text.Json.JsonSerializerOptions.Web))!.AsObject();
        corpo["edicao"] = $"teste-{Guid.NewGuid():N}"[..20];
        corpo["fonteOficial"] = "Cartão oficial (teste)";
        corpo["requisitos"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(requisitos, System.Text.Json.JsonSerializerOptions.Web));
        var rascunho = await Esperar(await Admin.PostAsJsonAsync("/api/RevisoesRequisitos", corpo), HttpStatusCode.Created);
        Assert.Equal("Rascunho", (string)rascunho["situacao"]!);
        return await Esperar(await Admin.PostAsJsonAsync($"/api/RevisoesRequisitos/{Id(rascunho)}/Publicar", new { versao = Versao(rascunho) }));
    }

    private static object Item(string codigo, object? exigencia = null, bool? obrigatorio = null) =>
        new { codigo, descricao = $"Requisito {codigo}", obrigatorio, exigenciaEspecialidade = exigencia };

    private static object Grupo(string codigo, string regra, int? quantidadeMinima, params object[] subitens) =>
        new { codigo, descricao = $"Grupo {codigo}", regra, quantidadeMinima, subitens };

    private async Task<JsonObject> AssociarClasseAsync(Guid membroId, string classe, DateOnly? inicio = null, string modalidade = "Padrao",
        HttpStatusCode esperado = HttpStatusCode.Created) =>
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Classes",
            new { membroId, classe, modalidade, dataInicio = D(inicio ?? Hoje.AddDays(-30)) }), esperado);

    private async Task<JsonObject> AssociarEspecialidadeAsync(Guid membroId, Guid especialidadeId, DateOnly? inicio = null,
        HttpStatusCode esperado = HttpStatusCode.Created) =>
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades",
            new { membroId, especialidadeId, dataInicio = D(inicio ?? Hoje.AddDays(-30)) }), esperado);

    private Task<HttpResponseMessage> RequisitoAsync(string recurso, Guid vinculo, Guid requisito, string situacao, DateOnly? cumprimento = null,
        string? justificativa = null) =>
        Admin.PutAsJsonAsync($"/api/Formacao/{recurso}/{vinculo}/Requisitos/{requisito}",
            new { situacao, dataCumprimento = cumprimento is { } c ? D(c) : null, justificativa });

    private async Task<JsonObject> ObterAsync(string recurso, Guid id) => await Esperar(await Admin.GetAsync($"/api/Formacao/{recurso}/{id}"));

    private async Task<JsonObject> ConcluirAsync(string recurso, Guid id, DateOnly? data = null, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        var atual = await ObterAsync(recurso, id);
        return await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/{recurso}/{id}/Concluir",
            new { dataConclusao = D(data ?? Hoje), versao = Versao(atual) }), esperado);
    }

    private async Task<JsonObject> OperacaoAsync(string recurso, Guid id, string operacao, HttpStatusCode esperado = HttpStatusCode.OK,
        string justificativa = "correção registrada no teste")
    {
        var atual = await ObterAsync(recurso, id);
        return await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/{recurso}/{id}/{operacao}",
            new { justificativa, versao = Versao(atual) }), esperado);
    }

    /// <summary>Especialidade concluída para o membro, com revisão de um único requisito validado.</summary>
    private async Task<(Guid Especialidade, Guid Vinculo)> EspecialidadeConcluidaAsync(Guid membroId, string area, DateOnly? conclusao = null)
    {
        var especialidade = await EspecialidadeAsync(area);
        await PublicarAsync(new { especialidadeId = Id(especialidade) }, Item("1"));
        var vinculo = await AssociarEspecialidadeAsync(membroId, Id(especialidade), Hoje.AddDays(-20));
        await Esperar(await RequisitoAsync("Especialidades", Id(vinculo), Req(vinculo, "1"), "Validado"));
        await ConcluirAsync("Especialidades", Id(vinculo), conclusao ?? Hoje);
        return (Id(especialidade), Id(vinculo));
    }

    private async Task HistoricoAsync(Guid membroId, string classe, DateOnly? conclusao = null) =>
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Classes/Historicos", new
        {
            membroId, classe, origemHistorica = "Cartão arquivado na secretaria", investiduraOuEntregaRealizada = false,
            dataConclusao = conclusao is { } c ? D(c) : null,
        }), HttpStatusCode.Created);

    [Fact]
    public async Task Especialidades_DuplicidadeInativacaoEReativacao_PreservamVinculosEHistorico()
    {
        var codigo = $"T-{Random.Shared.Next(100000, 999999)}";
        var nome = $"Botânica {Guid.NewGuid():N}"[..30];
        var especialidade = await EspecialidadeAsync("Estudos da Natureza", codigo, nome);
        var id = Id(especialidade);

        var codigoRepetido = await Admin.PostAsJsonAsync("/api/Especialidades", new { nome = "Outra", codigoOficial = codigo.ToLowerInvariant(), area = "ADRA", fonteOficial = "f" });
        Assert.Equal(HttpStatusCode.Conflict, codigoRepetido.StatusCode);
        var nomeRepetido = await Admin.PostAsJsonAsync("/api/Especialidades", new { nome = nome.ToUpperInvariant(), area = "Estudos da Natureza", modalidade = "Basica", fonteOficial = "f" });
        Assert.Equal(HttpStatusCode.Conflict, nomeRepetido.StatusCode);
        await EspecialidadeAsync("Artes e Habilidades Manuais", nome: nome);
        var avancada = await Esperar(await Admin.PostAsJsonAsync("/api/Especialidades",
            new { nome, area = "Estudos da Natureza", modalidade = "Avancada", fonteOficial = "f", preRequisitos = new[] { id } }), HttpStatusCode.Created);
        Assert.Equal(id, Id(avancada["preRequisitos"]![0]!));

        var ciclo = await Admin.PutAsJsonAsync($"/api/Especialidades/{id}", new
        {
            nome, codigoOficial = codigo, area = "Estudos da Natureza", modalidade = "Basica", fonteOficial = "f", preRequisitos = new[] { Id(avancada) },
            versao = Versao(especialidade),
        });
        Assert.Equal(HttpStatusCode.BadRequest, ciclo.StatusCode);

        var atualizada = await Esperar(await Admin.PutAsJsonAsync($"/api/Especialidades/{id}", new
        {
            nome, codigoOficial = codigo, area = "Estudos da Natureza", modalidade = "Basica", fonteOficial = "Manual 2024", descricao = "Nova descrição",
            versao = Versao(especialidade),
        }));
        Assert.Equal("Nova descrição", (string)atualizada["descricao"]!);
        var desatualizada = await Admin.PutAsJsonAsync($"/api/Especialidades/{id}", new
        {
            nome, area = "Estudos da Natureza", fonteOficial = "f", versao = Versao(especialidade),
        });
        Assert.Equal(HttpStatusCode.Conflict, desatualizada.StatusCode);

        var busca = await Esperar(await Admin.GetAsync($"/api/Especialidades?busca={codigo}&area=estudos da natureza&pageSize=1"));
        Assert.Equal((1, 1), ((int)busca["total"]!, busca["items"]!.AsArray().Count));

        await PublicarAsync(new { especialidadeId = id }, Item("1"));
        var (_, membro) = await MembroAsync();
        var vinculo = await AssociarEspecialidadeAsync(membro, id);

        var inativada = await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Especialidades/{id}")
        {
            Content = JsonContent.Create(new { motivo = "Especialidade retirada do manual", versao = Versao(atualizada) }),
        }));
        Assert.Equal((false, "Especialidade retirada do manual"), ((bool)inativada["ativo"]!, (string)inativada["motivoInativacao"]!));
        Assert.Equal(0, (int)(await Esperar(await Admin.GetAsync($"/api/Especialidades?busca={codigo}")))["total"]!);
        Assert.Equal(1, (int)(await Esperar(await Admin.GetAsync($"/api/Especialidades?busca={codigo}&incluirInativas=true")))["total"]!);

        var (_, outro) = await MembroAsync();
        await AssociarEspecialidadeAsync(outro, id, esperado: HttpStatusCode.Conflict);
        var preservado = await ObterAsync("Especialidades", Id(vinculo));
        Assert.Equal(("EmAndamento", false), ((string)preservado["situacao"]!, (bool)preservado["especialidade"]!["ativo"]!));
        Assert.Equal(HttpStatusCode.OK, (await RequisitoAsync("Especialidades", Id(vinculo), Req(preservado, "1"), "Validado")).StatusCode);

        var reativada = await Esperar(await Admin.PostAsJsonAsync($"/api/Especialidades/{id}/Reativar", new { versao = Versao(inativada) }));
        Assert.True((bool)reativada["ativo"]!);

        var acoes = await Db(db => db.HistoricoFormacao.Where(h => h.Tabela == "especialidades" && h.RegistroId == id)
            .OrderBy(h => h.OcorridoEmUtc).Select(h => new { h.Acao, h.Motivo, h.IpResponsavel }).ToListAsync());
        Assert.Equal(["CADASTRAR_ESPECIALIDADE", "ATUALIZAR_ESPECIALIDADE", "INATIVAR_ESPECIALIDADE", "REATIVAR_ESPECIALIDADE"], acoes.Select(a => a.Acao));
        Assert.Equal("Especialidade retirada do manual", acoes[2].Motivo);
        Assert.All(acoes, a => Assert.Equal(FormacaoSqlFixture.Ip, a.IpResponsavel));
    }

    [Fact]
    public async Task Classe_FluxoCompleto_GruposAlternativas_ConclusaoSeparadaDaInvestidura_EReabertura()
    {
        await PublicarAsync(new { classe = "Companheiro" },
            Item("1"), Grupo("2", "Alternativas", 1, Item("2a"), Item("2b")), Item("3", obrigatorio: false));
        var (participante, membro) = await MembroAsync();
        var vinculo = await AssociarClasseAsync(membro, "Companheiro", Hoje.AddDays(-30));
        var id = Id(vinculo);

        var progresso = vinculo["progressoDetalhado"]!;
        Assert.Equal((2, 0, 0, false), ((int)progresso["unidadesObrigatorias"]!, (int)progresso["unidadesSatisfeitas"]!,
            (int)progresso["percentual"]!, (bool)progresso["podeConcluir"]!));
        var bloqueada = await ConcluirAsync("Classes", id, esperado: HttpStatusCode.Conflict);
        Assert.Equal(["1", "2"], bloqueada["requisitosPendentes"]!.AsArray().Select(n => (string)n!));

        Assert.Equal(HttpStatusCode.Conflict, (await RequisitoAsync("Classes", id, Req(vinculo, "2"), "Validado")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RequisitoAsync("Classes", id, Req(vinculo, "1"), "Validado", Hoje.AddDays(-31))).StatusCode);
        await Esperar(await RequisitoAsync("Classes", id, Req(vinculo, "1"), "Validado", Hoje.AddDays(-5)));
        var parcial = await Esperar(await RequisitoAsync("Classes", id, Req(vinculo, "2a"), "EmAndamento"));
        Assert.Equal(50, (int)parcial["progressoDetalhado"]!["percentual"]!);
        var completo = await Esperar(await RequisitoAsync("Classes", id, Req(vinculo, "2b"), "Validado", Hoje.AddDays(-3)));
        Assert.Equal((100, true), ((int)completo["progressoDetalhado"]!["percentual"]!, (bool)completo["progressoDetalhado"]!["podeConcluir"]!));
        var item = completo["requisitos"]!.AsArray().Single(r => (string)r!["codigo"]! == "1")!;
        Assert.Equal(("Validado", "Administrador"), ((string)item["situacao"]!, (string)item["validadoPor"]!["nome"]!));
        Assert.Equal("EmAndamento", (string)(await ObterAsync("Classes", id))["situacao"]!);

        var cedo = await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{id}/Investidura", new { data = D(Hoje), versao = Versao(completo) });
        Assert.Equal(HttpStatusCode.Conflict, cedo.StatusCode);
        await ConcluirAsync("Classes", id, Hoje.AddDays(-4), HttpStatusCode.BadRequest);
        var concluida = await ConcluirAsync("Classes", id, Hoje.AddDays(-2));
        Assert.Equal(("Concluida", true), ((string)concluida["situacao"]!, (bool)concluida["aguardandoInvestiduraOuEntrega"]!));
        Assert.Null(concluida["investidura"]);
        Assert.Equal(HttpStatusCode.Conflict, (await RequisitoAsync("Classes", id, Req(vinculo, "1"), "Pendente", justificativa: "x")).StatusCode);

        var aguardando = await Esperar(await Admin.GetAsync($"/api/Formacao/Classes?membroId={membro}&aguardandoInvestiduraOuEntrega=true"));
        Assert.Equal(id, Id(aguardando["items"]![0]!));

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{id}/Investidura",
            new { data = D(Hoje.AddDays(-3)), versao = Versao(concluida) })).StatusCode);
        var investida = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{id}/Investidura", new { data = D(Hoje), versao = Versao(concluida) }));
        Assert.Equal((D(Hoje), false), ((string)investida["investidura"]!["data"]!, (bool)investida["aguardandoInvestiduraOuEntrega"]!));

        var meus = await Esperar(await participante.GetAsync("/api/Me/Formacao"));
        Assert.Equal(id, Id(meus["classes"]![0]!));
        var meu = await Esperar(await participante.GetAsync($"/api/Me/Formacao/Classes/{id}"));
        Assert.Equal(100, (int)meu["progressoDetalhado"]!["percentual"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await participante.PutAsJsonAsync($"/api/Formacao/Classes/{id}/Requisitos/{Req(vinculo, "3")}",
            new { situacao = "Validado" })).StatusCode);

        var reaberta = await OperacaoAsync("Classes", id, "Reabrir", justificativa: "Requisito 2b conferido por engano");
        Assert.Equal("EmAndamento", (string)reaberta["situacao"]!);
        Assert.Null(reaberta["investidura"]);
        Assert.Null(reaberta["conclusao"]);
        var historico = await Esperar(await Admin.GetAsync($"/api/Formacao/Classes/{id}/Historico"));
        var reabertura = historico["items"]!.AsArray().Single(h => (string)h!["acao"]! == "REABRIR")!;
        Assert.Equal(("Requisito 2b conferido por engano", D(Hoje), "membros_classes"),
            ((string)reabertura["motivo"]!, (string)reabertura["estadoAnterior"]!["DataInvestidura"]!, (string)reabertura["tabela"]!));
        Assert.Null(reabertura["estadoNovo"]!["DataInvestidura"]);
        Assert.Contains(historico["items"]!.AsArray(), h => (string)h!["acao"]! == "ATUALIZAR_REQUISITO" && (string)h["tabela"]! == "progresso_requisitos");
    }

    [Fact]
    public async Task Vinculos_DuplicidadeCancelamentoERetomada_PreservamProgresso_SemConquistaDuplicada()
    {
        await PublicarAsync(new { classe = "Pioneiro" }, Item("1"), Item("2"));
        var (_, membro) = await MembroAsync();
        var primeiro = await AssociarClasseAsync(membro, "Pioneiro");
        await AssociarClasseAsync(membro, "Pioneiro", esperado: HttpStatusCode.Conflict);
        await Esperar(await RequisitoAsync("Classes", Id(primeiro), Req(primeiro, "1"), "Validado"));

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{Id(primeiro)}/Cancelar",
            new { versao = Versao(await ObterAsync("Classes", Id(primeiro))) })).StatusCode);
        var cancelado = await OperacaoAsync("Classes", Id(primeiro), "Cancelar", justificativa: "Mudou de unidade");
        Assert.Equal(("Cancelada", "Mudou de unidade"), ((string)cancelado["situacao"]!, (string)cancelado["cancelamento"]!["motivo"]!));
        Assert.Equal(HttpStatusCode.Conflict, (await RequisitoAsync("Classes", Id(primeiro), Req(primeiro, "2"), "Validado")).StatusCode);

        var segundo = await AssociarClasseAsync(membro, "Pioneiro");
        await OperacaoAsync("Classes", Id(primeiro), "Retomar", HttpStatusCode.Conflict);
        await OperacaoAsync("Classes", Id(segundo), "Cancelar");
        var retomado = await OperacaoAsync("Classes", Id(primeiro), "Retomar");
        Assert.Equal(("EmAndamento", 1), ((string)retomado["situacao"]!, (int)retomado["progressoDetalhado"]!["unidadesSatisfeitas"]!));
        Assert.Null(retomado["cancelamento"]);
        Assert.Equal(1, await Db(db => db.MembrosClasses.CountAsync(v => v.MembroId == membro && v.Situacao != SituacaoVinculo.Cancelada)));
        Assert.Equal(2, await Db(db => db.MembrosClasses.CountAsync(v => v.MembroId == membro)));
    }

    [Fact]
    public async Task Evidencias_ExigemConquistaValida_SemDuplaContagem_EBloqueiamReaberturaDaEspecialidade()
    {
        var area = $"Área {Guid.NewGuid():N}"[..20];
        var (_, membro) = await MembroAsync();
        var (_, especialidadeVinculo) = await EspecialidadeConcluidaAsync(membro, area, Hoje.AddDays(-1));
        var exigencia = new { quantidade = 1, area, naoRealizadaAnteriormente = true };
        await PublicarAsync(new { classe = "Pesquisador" }, Item("4", exigencia), Item("5", exigencia, obrigatorio: false));
        var classe = await AssociarClasseAsync(membro, "Pesquisador", Hoje.AddDays(-10));
        var id = Id(classe);

        Assert.Equal(HttpStatusCode.Conflict, (await RequisitoAsync("Classes", id, Req(classe, "4"), "Validado")).StatusCode);
        var (_, outroMembro) = await MembroAsync();
        var (_, alheia) = await EspecialidadeConcluidaAsync(outroMembro, area);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{id}/Requisitos/{Req(classe, "4")}/Evidencias",
            new { membroEspecialidadeId = alheia })).StatusCode);

        var comEvidencia = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{id}/Requisitos/{Req(classe, "4")}/Evidencias",
            new { membroEspecialidadeId = especialidadeVinculo }));
        var evidencia = comEvidencia["requisitos"]!.AsArray().Single(r => (string)r!["codigo"]! == "4")!["evidencias"]![0]!;
        Assert.True((bool)evidencia["valida"]!);
        var duplicada = await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{id}/Requisitos/{Req(classe, "5")}/Evidencias",
            new { membroEspecialidadeId = especialidadeVinculo });
        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);

        await Esperar(await RequisitoAsync("Classes", id, Req(classe, "4"), "Validado"));
        var remocao = await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Formacao/Classes/{id}/Requisitos/{Req(classe, "4")}/Evidencias/{especialidadeVinculo}")
        { Content = JsonContent.Create(new { justificativa = "teste" }) });
        Assert.Equal(HttpStatusCode.Conflict, remocao.StatusCode);
        await ConcluirAsync("Classes", id, Hoje);

        var dependentes = await OperacaoAsync("Especialidades", especialidadeVinculo, "Reabrir", HttpStatusCode.Conflict);
        Assert.Contains("Pesquisador", (string)dependentes["dependentes"]![0]!);

        await PublicarAsync(new { classe = "Excursionista" }, Item("1", exigencia));
        var outraClasse = await AssociarClasseAsync(membro, "Excursionista", Hoje.AddDays(-10));
        var reuso = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{Id(outraClasse)}/Requisitos/{Req(outraClasse, "1")}/Evidencias",
            new { membroEspecialidadeId = especialidadeVinculo }), HttpStatusCode.Conflict);
        Assert.Contains("outra classe", (string)reuso["detail"]!);
    }

    [Fact]
    public async Task Especialidades_PreRequisitoBloqueiaConclusao_EEntregaEhRegistradaSeparadamente()
    {
        var area = $"Área {Guid.NewGuid():N}"[..20];
        var basica = await EspecialidadeAsync(area);
        var avancada = await EspecialidadeAsync(area, preRequisitos: Id(basica));
        await PublicarAsync(new { especialidadeId = Id(basica) }, Item("1"));
        await PublicarAsync(new { especialidadeId = Id(avancada) }, Item("1"));
        var (_, membro) = await MembroAsync();

        var vinculo = await AssociarEspecialidadeAsync(membro, Id(avancada));
        await Esperar(await RequisitoAsync("Especialidades", Id(vinculo), Req(vinculo, "1"), "Validado"));
        var bloqueio = await ConcluirAsync("Especialidades", Id(vinculo), esperado: HttpStatusCode.Conflict);
        Assert.False((bool)bloqueio["preRequisitos"]![0]!["atendido"]!);

        var basicaVinculo = await AssociarEspecialidadeAsync(membro, Id(basica));
        await Esperar(await RequisitoAsync("Especialidades", Id(basicaVinculo), Req(basicaVinculo, "1"), "Validado"));
        await ConcluirAsync("Especialidades", Id(basicaVinculo), Hoje.AddDays(-1));
        var concluida = await ConcluirAsync("Especialidades", Id(vinculo), Hoje);
        Assert.Null(concluida["entrega"]);
        var entregue = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Especialidades/{Id(vinculo)}/Entrega", new { data = D(Hoje), versao = Versao(concluida) }));
        Assert.Equal(D(Hoje), (string)entregue["entrega"]!["data"]!);
        Assert.Null(entregue["investidura"]);

        await OperacaoAsync("Especialidades", Id(basicaVinculo), "Reabrir", HttpStatusCode.Conflict);
        var desfeita = await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Formacao/Especialidades/{Id(vinculo)}/Entrega")
        { Content = JsonContent.Create(new { justificativa = "Distintivo devolvido", versao = Versao(entregue) }) }));
        Assert.Equal(("Concluida", true), ((string)desfeita["situacao"]!, (bool)desfeita["aguardandoInvestiduraOuEntrega"]!));
    }

    [Fact]
    public async Task Lideranca_AplicaRegrasDeInicio_ValidacoesExternasEInvestidura_SemDeclararAprovacaoOficialPelaConclusaoLocal()
    {
        await PublicarAsync(new { classe = "Lider" }, Item("1"));
        var (_, membro) = await MembroAsync(nascimento: Hoje.AddYears(-15));
        var jovem = await AssociarClasseAsync(membro, "Lider", Hoje, esperado: HttpStatusCode.Conflict);
        Assert.Equal(2, jovem["preRequisitos"]!.AsArray().Count);

        await NascimentoAsync(membro, Hoje.AddYears(-17));
        var semRegulares = await AssociarClasseAsync(membro, "Lider", Hoje, esperado: HttpStatusCode.Conflict);
        Assert.Contains("faltam", (string)semRegulares["preRequisitos"]![0]!["detalhe"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync("/api/Formacao/Classes",
            new { membroId = membro, classe = "Lider", modalidade = "Agrupada", dataInicio = D(Hoje) })).StatusCode);

        foreach (var regular in new[] { "Amigo", "Companheiro", "Pesquisador", "Pioneiro", "Excursionista", "Guia" })
        {
            await HistoricoAsync(membro, regular, Hoje.AddYears(-2));
        }

        var lider = await AssociarClasseAsync(membro, "Lider", Hoje.AddDays(-5));
        Assert.Equal(4, lider["validacoesExternas"]!.AsArray().Count);
        await Esperar(await RequisitoAsync("Classes", Id(lider), Req(lider, "1"), "Validado"));
        var concluida = await ConcluirAsync("Classes", Id(lider), Hoje.AddDays(-1));
        Assert.False((bool)concluida["aprovacaoOficialConfirmada"]!);

        var bloqueada = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{Id(lider)}/Investidura",
            new { data = D(Hoje), versao = Versao(concluida) }), HttpStatusCode.Conflict);
        Assert.Equal(5, bloqueada["preRequisitos"]!.AsArray().Count);

        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{Id(lider)}/ValidacoesExternas/INEXISTENTE",
            new { confirmada = true, data = D(Hoje) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{Id(lider)}/ValidacoesExternas/APROVACAO_OFICIAL",
            new { confirmada = true })).StatusCode);
        foreach (var codigo in new[] { "MEMBRO_BATIZADO", "RECOMENDACAO_COMISSAO_IGREJA", "CADASTRO_SGC", "APROVACAO_OFICIAL" })
        {
            await Esperar(await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{Id(lider)}/ValidacoesExternas/{codigo.ToLowerInvariant()}",
                new { confirmada = true, data = D(Hoje), referencia = $"Documento {codigo}" }));
        }

        var menorDe18 = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{Id(lider)}/Investidura",
            new { data = D(Hoje), versao = Versao(await ObterAsync("Classes", Id(lider))) }), HttpStatusCode.Conflict);
        Assert.Contains("18 anos", (string)menorDe18["preRequisitos"]![0]!["descricao"]!);

        await NascimentoAsync(membro, Hoje.AddYears(-19));
        var investida = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{Id(lider)}/Investidura",
            new { data = D(Hoje), versao = Versao(await ObterAsync("Classes", Id(lider))) }));
        Assert.True((bool)investida["aprovacaoOficialConfirmada"]!);

        var desfazer = await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{Id(lider)}/ValidacoesExternas/APROVACAO_OFICIAL",
            new { confirmada = false, justificativa = "teste" });
        Assert.Equal(HttpStatusCode.Conflict, desfazer.StatusCode);

        await PublicarAsync(new { classe = "LiderMaster" }, Item("1"));
        var semExperiencia = await AssociarClasseAsync(membro, "LiderMaster", Hoje, esperado: HttpStatusCode.Conflict);
        Assert.Contains(semExperiencia["preRequisitos"]!.AsArray(), p => ((string?)p!["detalhe"])?.StartsWith("início possível a partir de") == true);

        var amigo = (await Esperar(await Admin.GetAsync($"/api/Formacao/Classes?membroId={membro}&classe=Amigo")))["items"]![0]!;
        var dependente = await OperacaoAsync("Classes", Id(amigo), "Cancelar", HttpStatusCode.Conflict);
        Assert.Contains("Líder", (string)dependente["dependentes"]![0]!);
    }

    [Fact]
    public async Task RegistroHistorico_NaoFabricaDatasNemValidacoes_EPodeSerCanceladoComJustificativa()
    {
        var especialidade = await EspecialidadeAsync();
        var (participante, membro) = await MembroAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync("/api/Formacao/Especialidades/Historicos", new
        {
            membroId = membro, especialidadeId = Id(especialidade), origemHistorica = "SGC", investiduraOuEntregaRealizada = false,
            dataConclusao = D(Hoje.AddDays(10)),
        })).StatusCode);

        var historico = await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades/Historicos", new
        {
            membroId = membro, especialidadeId = Id(especialidade), origemHistorica = "Cartão de 2019 arquivado", investiduraOuEntregaRealizada = true,
        }), HttpStatusCode.Created);
        Assert.Equal((true, "Concluida", "Cartão de 2019 arquivado", 0), ((bool)historico["historico"]!, (string)historico["situacao"]!,
            (string)historico["origemHistorica"]!, historico["requisitos"]!.AsArray().Count));
        Assert.Null(historico["progresso"]);
        Assert.Null(historico["revisao"]);
        Assert.Null(historico["conclusao"]!["data"]);
        Assert.Null(historico["entrega"]!["data"]);
        Assert.Equal("Administrador", (string)historico["criadoPor"]!["nome"]!);
        Assert.Equal(0, await Db(db => db.ProgressoRequisitos.CountAsync(p => p.MembroEspecialidadeId == Id(historico))));

        Assert.Equal(HttpStatusCode.OK, (await participante.GetAsync($"/api/Me/Formacao/Especialidades/{Id(historico)}")).StatusCode);
        await OperacaoAsync("Especialidades", Id(historico), "Reabrir", HttpStatusCode.Conflict);
        var cancelado = await OperacaoAsync("Especialidades", Id(historico), "Cancelar", justificativa: "Lançado para o membro errado");
        Assert.Equal("Cancelada", (string)cancelado["situacao"]!);
        Assert.Null(cancelado["conclusao"]);
        await OperacaoAsync("Especialidades", Id(historico), "Retomar", HttpStatusCode.Conflict);
        var anterior = await Db(db => db.HistoricoFormacao.SingleAsync(h => h.VinculoId == Id(historico) && h.Acao == "CANCELAR"));
        Assert.Contains("\"EntregaRegistradaPorUsuarioId\"", anterior.EstadoAnteriorJson);
    }

    [Fact]
    public async Task Isolamento_ParticipanteSoConsultaOProprio_InstrutorETesoureiroNaoEscrevem()
    {
        await PublicarAsync(new { classe = "Guia" }, Item("1"));
        var (participante, membro) = await MembroAsync();
        var (instrutor, instrutorId) = await MembroAsync();
        var (tesoureiro, _) = await MembroAsync("TES");
        var (outro, outroId) = await MembroAsync();
        var vinculo = await AssociarClasseAsync(membro, "Guia");
        var atualizado = await Esperar(await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{Id(vinculo)}",
            new { instrutorId, observacoes = "Turma de sábado", versao = Versao(vinculo) }));
        Assert.Equal(instrutorId, Id(atualizado["instrutor"]!));
        var doOutro = await AssociarClasseAsync(outroId, "Guia");

        Assert.Equal(HttpStatusCode.NotFound, (await participante.GetAsync($"/api/Me/Formacao/Classes/{Id(doOutro)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outro.GetAsync($"/api/Me/Formacao/Classes/{Id(vinculo)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await participante.GetAsync($"/api/Me/Formacao/Especialidades/{Id(vinculo)}")).StatusCode);
        var meus = await Esperar(await participante.GetAsync("/api/Me/Formacao"));
        Assert.Equal([Id(vinculo)], meus["classes"]!.AsArray().Select(c => Id(c!)));

        foreach (var cliente in new[] { participante, instrutor, tesoureiro })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync($"/api/Formacao/Classes/{Id(vinculo)}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync($"/api/Formacao/Classes?membroId={membro}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync($"/api/Formacao/Membros/{membro}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.PutAsJsonAsync($"/api/Formacao/Classes/{Id(vinculo)}/Requisitos/{Req(vinculo, "1")}",
                new { situacao = "Validado" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/api/Classes/Guia")).StatusCode);
        }

        Assert.Equal(0, await Db(db => db.ProgressoRequisitos.CountAsync(p => p.MembroClasseId == Id(vinculo))));
        var (secretario, _) = await MembroAsync("SEC");
        Assert.Equal(HttpStatusCode.OK, (await RequisitoViaAsync(secretario, Id(vinculo), Req(vinculo, "1"))).StatusCode);
    }

    private static Task<HttpResponseMessage> RequisitoViaAsync(HttpClient client, Guid vinculo, Guid requisito) =>
        client.PutAsJsonAsync($"/api/Formacao/Classes/{vinculo}/Requisitos/{requisito}", new { situacao = "Validado" });

    [Fact]
    public async Task Revisoes_PublicacaoSubstituiAnterior_VinculosMantemARevisao_MigracaoExplicita_ERequisitosImutaveis()
    {
        var (participante, _) = await MembroAsync();
        var semRevisao = await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Classes",
            new { membroId = (await MembroAsync()).Id, classe = "ExcursionistaNaMata", dataInicio = D(Hoje) }), HttpStatusCode.Conflict);
        Assert.Contains("revisão publicada", (string)semRevisao["title"]!);

        var especialidade = await EspecialidadeAsync();
        var primeira = await PublicarAsync(new { especialidadeId = Id(especialidade) }, Item("1"), Item("2"));
        var (_, membro) = await MembroAsync();
        var vinculo = await AssociarEspecialidadeAsync(membro, Id(especialidade));
        await Esperar(await RequisitoAsync("Especialidades", Id(vinculo), Req(vinculo, "1"), "Validado"));

        var segunda = await PublicarAsync(new { especialidadeId = Id(especialidade) }, Item("1"), Item("2"), Item("3"));
        Assert.Equal("Substituida", (string)(await Esperar(await Admin.GetAsync($"/api/RevisoesRequisitos/{Id(primeira)}")))["situacao"]!);
        Assert.Equal(Id(segunda), Id((await Esperar(await Admin.GetAsync($"/api/Especialidades/{Id(especialidade)}")))["revisaoVigente"]!));
        var mantido = await ObterAsync("Especialidades", Id(vinculo));
        Assert.Equal((Id(primeira), 1), (Id(mantido["revisao"]!), (int)mantido["progressoDetalhado"]!["unidadesSatisfeitas"]!));

        var rascunho = await Esperar(await Admin.PostAsJsonAsync("/api/RevisoesRequisitos", new
        {
            especialidadeId = Id(especialidade), edicao = "rascunho", fonteOficial = "f", requisitos = new[] { Item("1") },
        }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NotFound, (await participante.GetAsync($"/api/RevisoesRequisitos/{Id(rascunho)}")).StatusCode);
        Assert.Equal(2, (await Esperar(await participante.GetAsync($"/api/Especialidades/{Id(especialidade)}/Revisoes")))["items"]!.AsArray().Count);
        Assert.Equal(3, (await Esperar(await Admin.GetAsync($"/api/Especialidades/{Id(especialidade)}/Revisoes")))["items"]!.AsArray().Count);
        await Esperar(await Admin.PostAsJsonAsync($"/api/RevisoesRequisitos/{Id(rascunho)}/Descartar", new { motivo = "Edição errada", versao = Versao(rascunho) }));
        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsJsonAsync($"/api/RevisoesRequisitos/{Id(segunda)}/Publicar",
            new { versao = Versao(segunda) })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsJsonAsync($"/api/Formacao/Especialidades/{Id(vinculo)}/MigrarRevisao",
            new { revisaoId = Id(primeira), justificativa = "x", versao = Versao(mantido) })).StatusCode);
        var migrado = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Especialidades/{Id(vinculo)}/MigrarRevisao",
            new { revisaoId = Id(segunda), justificativa = "Nova edição do manual", versao = Versao(mantido) }));
        Assert.Equal((Id(segunda), 0, 3), (Id(migrado["revisao"]!), (int)migrado["progressoDetalhado"]!["unidadesSatisfeitas"]!,
            (int)migrado["progressoDetalhado"]!["unidadesObrigatorias"]!));
        Assert.Equal(1, await Db(db => db.ProgressoRequisitos.CountAsync(p => p.MembroEspecialidadeId == Id(vinculo)
            && p.Situacao == SituacaoRequisito.Validado)));
        Assert.Equal("Nova edição do manual",
            (await Db(db => db.HistoricoFormacao.SingleAsync(h => h.VinculoId == Id(vinculo) && h.Acao == "MIGRAR_REVISAO"))).Motivo);

        var imutavel = await Assert.ThrowsAsync<SqlException>(() => Db(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.requisitos SET Descricao = 'adulterado' WHERE RevisaoId = {Id(primeira)}")));
        Assert.Equal(50126, imutavel.Number);
        var semContexto = await Assert.ThrowsAsync<SqlException>(() => Db(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.membros_especialidades SET Observacoes = 'sem auditoria' WHERE Id = {Id(vinculo)}")));
        Assert.Equal(50125, semContexto.Number);
    }

    [Fact]
    public async Task Consultas_FiltrosPorPeriodoGrupoSituacao_PaginacaoEstavel_EHistoricoIndividual()
    {
        await PublicarAsync(new { classe = "AmigoDaNatureza" }, Item("1"));
        var membros = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (_, id) = await MembroAsync();
            membros.Add(id);
            await AssociarClasseAsync(id, "AmigoDaNatureza", Hoje.AddDays(-100 - i), modalidade: "Agrupada");
        }

        var filtro = $"classe=AmigoDaNatureza&grupo=Avancada&periodo=Inicio&de={D(Hoje.AddDays(-102))}&ate={D(Hoje.AddDays(-100))}";
        var pagina1 = await Esperar(await Admin.GetAsync($"/api/Formacao/Classes?{filtro}&ordenarPor=Inicio&pageSize=2&page=1"));
        var pagina2 = await Esperar(await Admin.GetAsync($"/api/Formacao/Classes?{filtro}&ordenarPor=Inicio&pageSize=2&page=2"));
        Assert.Equal((3, 2), ((int)pagina1["total"]!, (int)pagina1["totalPages"]!));
        var datas = pagina1["items"]!.AsArray().Concat(pagina2["items"]!.AsArray()).Select(n => (string)n!["dataInicio"]!).ToList();
        Assert.Equal(datas.Order(), datas);
        Assert.Equal(3, datas.Distinct().Count());
        Assert.All(pagina1["items"]!.AsArray(), n => Assert.Equal(("Agrupada", "Avancada", "[\"1\"]"),
            ((string)n!["modalidade"]!, (string)n["classe"]!["grupo"]!, n["progresso"]!["requisitosPendentes"]!.ToJsonString())));

        Assert.Equal(0, (int)(await Esperar(await Admin.GetAsync($"/api/Formacao/Classes?{filtro}&situacao=Concluida")))["total"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/Formacao/Classes?periodo=Conclusao")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/Formacao/Classes?periodo=Inicio&de=2026-02-01&ate=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/Formacao/Especialidades?pageSize=101")).StatusCode);

        var relatorio = await Esperar(await Admin.GetAsync("/api/Formacao/Relatorios/Participantes?grupo=Avancada"));
        var linha = relatorio["items"]!.AsArray().Single(l => (string)l!["classe"]!["codigo"]! == "AmigoDaNatureza")!;
        Assert.True((int)linha["emAndamento"]! >= 3);
        Assert.Equal(6, relatorio["items"]!.AsArray().Count);

        var individual = await Esperar(await Admin.GetAsync($"/api/Formacao/Membros/{membros[0]}"));
        Assert.Equal(("AmigoDaNatureza", 0), ((string)individual["classes"]![0]!["classe"]!["codigo"]!, individual["especialidades"]!.AsArray().Count));
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync($"/api/Formacao/Membros/{Guid.NewGuid()}")).StatusCode);
    }
}
