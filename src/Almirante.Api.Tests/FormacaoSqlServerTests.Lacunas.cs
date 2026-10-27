using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>
/// Issue #160: filtros, ordenações, relatório por especialidade e regras negativas da formação que a auditoria encontrou sem cenário.
/// </summary>
public sealed partial class FormacaoSqlServerTests
{
    private static string AreaUnica() => $"Área {Guid.NewGuid():N}"[..13];

    private async Task<JsonArray> ItensAsync(string url) => (await Esperar(await Admin.GetAsync(url)))["items"]!.AsArray();

    private async Task<JsonObject> EntregarAsync(Guid vinculo, DateOnly data, HttpStatusCode esperado = HttpStatusCode.OK) =>
        await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Especialidades/{vinculo}/Entrega",
            new { data = D(data), versao = Versao(await ObterAsync("Especialidades", vinculo)) }), esperado);

    [Fact]
    public async Task Especialidades_ListagemERelatorio_PorEspecialidadeArea_PeriodosOrdenacoesEHistorico()
    {
        var area = AreaUnica();
        var (_, m1) = await MembroAsync();
        var (_, m2) = await MembroAsync();
        var (especialidadeA, vinculoA) = await EspecialidadeConcluidaAsync(m1, area, Hoje.AddDays(-3));
        await EntregarAsync(vinculoA, Hoje.AddDays(-1));
        var (especialidadeB, vinculoB) = await EspecialidadeConcluidaAsync(m2, area, Hoje.AddDays(-2));
        var especialidadeC = Id(await EspecialidadeAsync(area));
        await PublicarAsync(new { especialidadeId = especialidadeC }, Item("1"));
        var vinculoC = Id(await AssociarEspecialidadeAsync(m1, especialidadeC, Hoje.AddDays(-10)));

        Assert.Equal([vinculoA], (await ItensAsync($"/api/Formacao/Especialidades?especialidadeId={especialidadeA}")).Select(Id));
        Assert.Equal(3, (await ItensAsync($"/api/Formacao/Especialidades?area={Uri.EscapeDataString(area)}&pageSize=100")).Count);

        var concluidas = await ItensAsync($"/api/Formacao/Especialidades?area={Uri.EscapeDataString(area)}&periodo=Conclusao" +
            $"&de={D(Hoje.AddDays(-3))}&ate={D(Hoje.AddDays(-2))}&ordenarPor=Conclusao");
        Assert.Equal([vinculoA, vinculoB], concluidas.Select(Id));

        var entregues = await ItensAsync($"/api/Formacao/Especialidades?area={Uri.EscapeDataString(area)}&periodo=InvestiduraOuEntrega" +
            $"&de={D(Hoje.AddDays(-1))}&ordenarPor=InvestiduraOuEntrega");
        Assert.Equal([vinculoA], entregues.Select(Id));

        var porEntrega = await ItensAsync($"/api/Formacao/Especialidades?area={Uri.EscapeDataString(area)}&ordenarPor=InvestiduraOuEntrega&pageSize=100");
        Assert.Equal(3, porEntrega.Count);
        Assert.Equal(vinculoA, Id(porEntrega[^1]!));
        Assert.Empty(await ItensAsync($"/api/Formacao/Especialidades?area={Uri.EscapeDataString(area)}&historico=true"));
        Assert.Equal(3, (await ItensAsync($"/api/Formacao/Especialidades?area={Uri.EscapeDataString(area)}&historico=false")).Count);

        var relatorio = (await Esperar(await Admin.GetAsync(
            $"/api/Formacao/Relatorios/Participantes?tipo=Especialidade&area={Uri.EscapeDataString(area)}")))["items"]!.AsArray();
        Assert.Equal(3, relatorio.Count);
        var linhaA = relatorio.Single(r => Id(r!["especialidade"]!) == especialidadeA)!;
        Assert.Equal((0, 1, 0, 0, 1), ((int)linhaA["emAndamento"]!, (int)linhaA["concluidas"]!, (int)linhaA["concluidasAguardandoInvestiduraOuEntrega"]!,
            (int)linhaA["canceladas"]!, (int)linhaA["total"]!));
        var linhaB = relatorio.Single(r => Id(r!["especialidade"]!) == especialidadeB)!;
        Assert.Equal(1, (int)linhaB["concluidasAguardandoInvestiduraOuEntrega"]!);
        Assert.Equal(1, (int)relatorio.Single(r => Id(r!["especialidade"]!) == especialidadeC)!["emAndamento"]!);

        var soA = (await Esperar(await Admin.GetAsync($"/api/Formacao/Relatorios/Participantes?tipo=Especialidade&especialidadeId={especialidadeA}")))["items"]!.AsArray();
        Assert.Equal(especialidadeA, Id(Assert.Single(soA)!["especialidade"]!));
        Assert.Equal(vinculoC, Id((await ItensAsync($"/api/Formacao/Especialidades?membroId={m1}&situacao=EmAndamento&area={Uri.EscapeDataString(area)}"))[0]!));
    }

    [Fact]
    public async Task Classes_FiltrosDeConclusaoEHistorico_EOrdenacaoPorConclusao()
    {
        var (_, membro) = await MembroAsync(nascimento: Hoje.AddYears(-12));
        await HistoricoAsync(membro, "Amigo", Hoje.AddYears(-1));
        await PublicarAsync(new { classe = "Companheiro" }, Item("1"));
        var companheiro = await AssociarClasseAsync(membro, "Companheiro", Hoje.AddDays(-20));
        await Esperar(await RequisitoAsync("Classes", Id(companheiro), Req(companheiro, "1"), "Validado"));
        await ConcluirAsync("Classes", Id(companheiro), Hoje.AddDays(-2));

        var historicas = await ItensAsync($"/api/Formacao/Classes?membroId={membro}&historico=true");
        Assert.Equal("Amigo", (string)Assert.Single(historicas)!["classe"]!["codigo"]!);
        var porConclusao = await ItensAsync($"/api/Formacao/Classes?membroId={membro}&periodo=Conclusao&ate={D(Hoje)}&ordenarPor=Conclusao");
        Assert.Equal(["Amigo", "Companheiro"], porConclusao.Select(v => (string)v!["classe"]!["codigo"]!));
    }

    [Fact]
    public async Task Associacao_RegrasDeMembroInstrutorDatasEspecialidadeERevisao()
    {
        var (_, membro) = await MembroAsync(nascimento: Hoje.AddYears(-12));
        var semRevisao = Id(await EspecialidadeAsync(AreaUnica()));

        var conflito = await AssociarEspecialidadeAsync(membro, semRevisao, esperado: HttpStatusCode.Conflict);
        Assert.Equal("Não há revisão publicada dos requisitos desta especialidade.", (string)conflito["title"]!);
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades",
            new { membroId = membro, especialidadeId = Guid.NewGuid(), dataInicio = D(Hoje) }), HttpStatusCode.NotFound);
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades",
            new { membroId = Guid.NewGuid(), especialidadeId = semRevisao, dataInicio = D(Hoje) }), HttpStatusCode.NotFound);

        await PublicarAsync(new { especialidadeId = semRevisao }, Item("1"));
        var instrutorInvalido = await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades",
            new { membroId = membro, especialidadeId = semRevisao, dataInicio = D(Hoje), instrutorId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        Assert.NotNull(instrutorInvalido["errors"]!["InstrutorId"]);
        var futura = await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades",
            new { membroId = membro, especialidadeId = semRevisao, dataInicio = D(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3)) }), HttpStatusCode.BadRequest);
        Assert.NotNull(futura["errors"]!["DataInicio"]);
        var antesDoNascimento = await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades",
            new { membroId = membro, especialidadeId = semRevisao, dataInicio = D(Hoje.AddYears(-13)) }), HttpStatusCode.BadRequest);
        Assert.Contains("nascimento", (string)antesDoNascimento["errors"]!["DataInicio"]![0]!);

        await AssociarEspecialidadeAsync(membro, semRevisao, Hoje.AddDays(-5));
        await AssociarEspecialidadeAsync(membro, semRevisao, Hoje.AddDays(-4), HttpStatusCode.Conflict);

        var (_, inativo) = await MembroAsync();
        await Db(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value={inativo};
            EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
            UPDATE dbo.Usuarios SET Ativo = 0 WHERE Id = {inativo};
            """));
        var membroInativo = await AssociarEspecialidadeAsync(inativo, semRevisao, esperado: HttpStatusCode.Conflict);
        Assert.Equal("Membro inativo.", (string)membroInativo["title"]!);

        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync($"/api/Formacao/Classes/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync($"/api/Formacao/Especialidades/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync($"/api/Formacao/Classes/{Guid.NewGuid()}/Historico")).StatusCode);
    }

    [Fact]
    public async Task RegistroHistorico_RecusaMembroInexistenteLiderancaAgrupadaEDuplicidades()
    {
        var (_, membro) = await MembroAsync(nascimento: Hoje.AddYears(-30));
        var especialidade = Id(await EspecialidadeAsync(AreaUnica()));

        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Classes/Historicos", new
        {
            membroId = Guid.NewGuid(), classe = "Amigo", origemHistorica = "Arquivo", investiduraOuEntregaRealizada = false,
        }), HttpStatusCode.NotFound);
        var agrupada = await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Classes/Historicos", new
        {
            membroId = membro, classe = "Lider", modalidade = "Agrupada", origemHistorica = "Arquivo", investiduraOuEntregaRealizada = false,
        }), HttpStatusCode.BadRequest);
        Assert.NotNull(agrupada["errors"]!["Modalidade"]);

        await HistoricoAsync(membro, "Amigo");
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Classes/Historicos", new
        {
            membroId = membro, classe = "Amigo", origemHistorica = "Arquivo", investiduraOuEntregaRealizada = false,
        }), HttpStatusCode.Conflict);

        var historicoEspecialidade = new { membroId = membro, especialidadeId = especialidade, origemHistorica = "Arquivo", investiduraOuEntregaRealizada = false };
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades/Historicos", historicoEspecialidade), HttpStatusCode.Created);
        await Esperar(await Admin.PostAsJsonAsync("/api/Formacao/Especialidades/Historicos", historicoEspecialidade), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Operacoes_RespeitamOEstadoDoVinculo()
    {
        var (_, membro) = await MembroAsync(nascimento: Hoje.AddYears(-12));
        var area = AreaUnica();
        var especialidade = Id(await EspecialidadeAsync(area));
        await PublicarAsync(new { especialidadeId = especialidade }, Item("1"), Item("2"));
        var vinculo = await AssociarEspecialidadeAsync(membro, especialidade, Hoje.AddDays(-10));
        var id = Id(vinculo);

        // Progresso já gravado é atualizado; retirar uma validação exige justificativa.
        await Esperar(await RequisitoAsync("Especialidades", id, Req(vinculo, "1"), "EmAndamento"));
        await Esperar(await RequisitoAsync("Especialidades", id, Req(vinculo, "1"), "Validado"));
        var semJustificativa = await Esperar(await RequisitoAsync("Especialidades", id, Req(vinculo, "1"), "EmAndamento"), HttpStatusCode.BadRequest);
        Assert.NotNull(semJustificativa["errors"]!["Justificativa"]);
        await Esperar(await RequisitoAsync("Especialidades", id, Guid.NewGuid(), "Validado"), HttpStatusCode.NotFound);
        var semExigencia = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Especialidades/{id}/Requisitos/{Req(vinculo, "1")}/Evidencias",
            new { membroEspecialidadeId = Guid.NewGuid() }), HttpStatusCode.NotFound);
        Assert.NotNull(semExigencia);

        // Data de conclusão fora do intervalo; entrega duplicada; desfazer entrega inexistente; cancelar concluído e cancelado.
        await Esperar(await RequisitoAsync("Especialidades", id, Req(vinculo, "2"), "Validado"));
        await ConcluirAsync("Especialidades", id, Hoje.AddDays(-20), HttpStatusCode.BadRequest);
        var entregaAntes = await EntregarAsync(id, Hoje, HttpStatusCode.Conflict);
        Assert.StartsWith("Só um vínculo concluído recebe", (string)entregaAntes["title"]!);
        await ConcluirAsync("Especialidades", id, Hoje);
        var cancelarConcluido = await OperacaoAsync("Especialidades", id, "Cancelar", HttpStatusCode.Conflict);
        Assert.Equal("Vínculo concluído.", (string)cancelarConcluido["title"]!);
        var semEntrega = await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Formacao/Especialidades/{id}/Entrega")
        {
            Content = JsonContent.Create(new { justificativa = "teste", versao = Versao(await ObterAsync("Especialidades", id)) }),
        }), HttpStatusCode.Conflict);
        Assert.StartsWith("Não há entrega", (string)semEntrega["title"]!);
        await EntregarAsync(id, Hoje);
        var duplicada = await EntregarAsync(id, Hoje, HttpStatusCode.Conflict);
        Assert.Equal("A entrega já foi registrada.", (string)duplicada["title"]!);

        // Cancelar duas vezes; retomar o que não está cancelado; retomar com outro vínculo ativo da mesma especialidade.
        var outra = Id(await EspecialidadeAsync(area));
        await PublicarAsync(new { especialidadeId = outra }, Item("1"));
        var primeiro = Id(await AssociarEspecialidadeAsync(membro, outra, Hoje.AddDays(-5)));
        await OperacaoAsync("Especialidades", primeiro, "Retomar", HttpStatusCode.Conflict);
        await OperacaoAsync("Especialidades", primeiro, "Cancelar");
        var jaCancelado = await OperacaoAsync("Especialidades", primeiro, "Cancelar", HttpStatusCode.Conflict);
        Assert.Equal("O vínculo já está cancelado.", (string)jaCancelado["title"]!);
        await AssociarEspecialidadeAsync(membro, outra, Hoje.AddDays(-4));
        await OperacaoAsync("Especialidades", primeiro, "Retomar", HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ValidacoesExternasEInvestidura_RegrasDeEstadoEDeLideranca()
    {
        var (_, membro) = await MembroAsync(nascimento: Hoje.AddYears(-40));
        foreach (var regular in new[] { "Amigo", "Companheiro", "Pesquisador", "Pioneiro", "Excursionista", "Guia" })
        {
            await HistoricoAsync(membro, regular, Hoje.AddYears(-5));
        }

        await PublicarAsync(new { classe = "Lider" }, Item("1"));
        var lider = await AssociarClasseAsync(membro, "Lider", Hoje.AddYears(-3));
        var idLider = Id(lider);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{idLider}/ValidacoesExternas/MEMBRO_BATIZADO",
            new { confirmada = true, data = D(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3)) })).StatusCode);
        await Esperar(await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{idLider}/ValidacoesExternas/MEMBRO_BATIZADO",
            new { confirmada = true, data = D(Hoje) }));
        var semJustificativa = await Esperar(await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{idLider}/ValidacoesExternas/MEMBRO_BATIZADO",
            new { confirmada = false }), HttpStatusCode.BadRequest);
        Assert.NotNull(semJustificativa["errors"]!["Justificativa"]);

        await OperacaoAsync("Classes", idLider, "Cancelar");
        var cancelado = await Esperar(await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{idLider}/ValidacoesExternas/CADASTRO_SGC",
            new { confirmada = true, data = D(Hoje) }), HttpStatusCode.Conflict);
        Assert.Equal("Vínculo cancelado.", (string)cancelado["title"]!);
        await OperacaoAsync("Classes", idLider, "Retomar");

        await Esperar(await RequisitoAsync("Classes", idLider, Req(lider, "1"), "Validado"));
        await ConcluirAsync("Classes", idLider, Hoje.AddYears(-2));
        foreach (var codigo in new[] { "RECOMENDACAO_COMISSAO_IGREJA", "CADASTRO_SGC", "APROVACAO_OFICIAL" })
        {
            await Esperar(await Admin.PutAsJsonAsync($"/api/Formacao/Classes/{idLider}/ValidacoesExternas/{codigo}",
                new { confirmada = true, data = D(Hoje.AddYears(-2)) }));
        }
        await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{idLider}/Investidura",
            new { data = D(Hoje.AddYears(-2)), versao = Versao(await ObterAsync("Classes", idLider)) }));

        // Líder Máster: experiência de um ano como líder investido; com o vínculo ativo, a investidura de Líder não pode ser desfeita.
        await PublicarAsync(new { classe = "LiderMaster" }, Item("1"));
        var master = await AssociarClasseAsync(membro, "LiderMaster", Hoje.AddYears(-1));
        var desfazer = await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Formacao/Classes/{idLider}/Investidura")
        {
            Content = JsonContent.Create(new { justificativa = "teste", versao = Versao(await ObterAsync("Classes", idLider)) }),
        }), HttpStatusCode.Conflict);
        Assert.Equal("A investidura de Líder sustenta o início de Líder Máster.", (string)desfazer["title"]!);

        // Prazo de conclusão do Líder Máster e Líder Máster Avançado exigindo o Líder Máster concluído.
        await Esperar(await RequisitoAsync("Classes", Id(master), Req(master, "1"), "Validado"));
        var avaliacao = await ObterAsync("Classes", Id(master));
        Assert.Contains(avaliacao["progressoDetalhado"]!["preRequisitos"]!.AsArray(), p => ((string)p!["descricao"]!).StartsWith("Concluir em até"));
        await PublicarAsync(new { classe = "LiderMasterAvancado" }, Item("1"));
        var avancado = await AssociarClasseAsync(membro, "LiderMasterAvancado", Hoje, esperado: HttpStatusCode.Conflict);
        Assert.Contains(avancado["preRequisitos"]!.AsArray(), p => ((string)p!["descricao"]!).Contains("Líder Máster"));

        // Migração de revisão de uma classe: nova revisão publicada do Líder Máster.
        var nova = await PublicarAsync(new { classe = "LiderMaster" }, Item("1"), Item("2"));
        var migrado = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{Id(master)}/MigrarRevisao",
            new { revisaoId = Id(nova), justificativa = "nova edição do cartão", versao = Versao(await ObterAsync("Classes", Id(master))) }));
        Assert.Equal(Id(nova), Guid.Parse((string)migrado["revisao"]!["id"]!));
    }

    [Fact]
    public async Task Classe_SemDataDeNascimento_NaoAtendeAIdadeMinima()
    {
        var (_, membro) = await MembroAsync();
        await PublicarAsync(new { classe = "Lider" }, Item("1"));
        var recusa = await AssociarClasseAsync(membro, "Lider", Hoje, esperado: HttpStatusCode.Conflict);
        Assert.Contains(recusa["preRequisitos"]!.AsArray(), p => (string?)p!["detalhe"] == "data de nascimento não cadastrada");
    }

    // O cadastro é um DTO fechado (versao não existe nele): campos nulos ficam fora do corpo.
    private static Dictionary<string, object?> ConteudoEspecialidade(string area, string? versao = null, string modalidade = "Basica",
        Guid[]? preRequisitos = null)
    {
        var corpo = new Dictionary<string, object?>
        {
            ["nome"] = $"Especialidade {Guid.NewGuid():N}"[..40], ["area"] = area, ["fonteOficial"] = "Manual de especialidades (teste)",
            ["edicaoRevisao"] = "2024", ["nivelDificuldade"] = 1, ["modalidade"] = modalidade,
        };
        if (preRequisitos is not null) corpo["preRequisitos"] = preRequisitos;
        if (versao is not null) corpo["versao"] = versao;
        return corpo;
    }

    private Task<HttpResponseMessage> AlterarSituacaoAsync(Guid id, bool ativar, string versao) => ativar
        ? Admin.PostAsJsonAsync($"/api/Especialidades/{id}/Reativar", new { versao })
        : Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Especialidades/{id}")
        {
            Content = JsonContent.Create(new { motivo = "retirada no teste", versao }),
        });

    [Fact]
    public async Task Catalogo_ModalidadePreRequisitosESituacaoDasEspecialidades()
    {
        var area = AreaUnica();
        var semPreRequisitos = await Esperar(await Admin.PostAsJsonAsync("/api/Especialidades", ConteudoEspecialidade(area)), HttpStatusCode.Created);
        Assert.Empty(semPreRequisitos["preRequisitos"]!.AsArray());
        var avancada = await Esperar(await Admin.PostAsJsonAsync("/api/Especialidades", ConteudoEspecialidade(area, modalidade: "Avancada")), HttpStatusCode.Created);
        var filtradas = (await Esperar(await Admin.GetAsync($"/api/Especialidades?area={Uri.EscapeDataString(area)}&modalidade=Avancada")))["items"]!.AsArray();
        Assert.Equal([Id(avancada)], filtradas.Select(Id));

        var id = Id(semPreRequisitos);
        var a = Id(await EspecialidadeAsync(area));
        var b = Id(await EspecialidadeAsync(area));
        async Task<JsonObject> PreRequisitosAsync(HttpStatusCode esperado, params Guid[] ids) =>
            await Esperar(await Admin.PutAsJsonAsync($"/api/Especialidades/{id}",
                ConteudoEspecialidade(area, Versao(await ObterEspecialidadeAsync(id)), preRequisitos: ids)), esperado);

        Assert.NotNull((await PreRequisitosAsync(HttpStatusCode.BadRequest, id))["errors"]!["PreRequisitos"]);
        Assert.NotNull((await PreRequisitosAsync(HttpStatusCode.BadRequest, Guid.NewGuid()))["errors"]!["PreRequisitos"]);
        Assert.Equal([a], (await PreRequisitosAsync(HttpStatusCode.OK, a))["preRequisitos"]!.AsArray().Select(Id));
        Assert.Equal([b], (await PreRequisitosAsync(HttpStatusCode.OK, b))["preRequisitos"]!.AsArray().Select(Id));
        Assert.Equal(new[] { a, b }.Order(), (await PreRequisitosAsync(HttpStatusCode.OK, a, b))["preRequisitos"]!.AsArray().Select(Id).Order());

        var jaAtiva = await Esperar(await AlterarSituacaoAsync(id, ativar: true, Versao(await ObterEspecialidadeAsync(id))), HttpStatusCode.Conflict);
        Assert.Equal("A especialidade já está ativa.", (string)jaAtiva["title"]!);
        await Esperar(await AlterarSituacaoAsync(id, ativar: false, Versao(await ObterEspecialidadeAsync(id))));
        var jaInativa = await Esperar(await AlterarSituacaoAsync(id, ativar: false, Versao(await ObterEspecialidadeAsync(id))), HttpStatusCode.Conflict);
        Assert.Equal("A especialidade já está inativa.", (string)jaInativa["title"]!);

        var inexistente = Guid.NewGuid();
        await Esperar(await Admin.PutAsJsonAsync($"/api/Especialidades/{inexistente}", ConteudoEspecialidade(area, "AAAAAAAAAAA=")), HttpStatusCode.NotFound);
        await Esperar(await AlterarSituacaoAsync(inexistente, ativar: false, "AAAAAAAAAAA="), HttpStatusCode.NotFound);
    }

    private async Task<JsonObject> ObterEspecialidadeAsync(Guid id) => await Esperar(await Admin.GetAsync($"/api/Especialidades/{id}"));

    [Fact]
    public async Task Revisoes_AlvoInexistenteOuInativo_ExigenciaInvalida_ERascunhoSoParaQuemGerencia()
    {
        var area = AreaUnica();
        async Task<JsonObject> CriarAsync(object alvo, HttpStatusCode esperado, params object[] requisitos)
        {
            var corpo = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(alvo, System.Text.Json.JsonSerializerOptions.Web))!.AsObject();
            corpo["edicao"] = "teste";
            corpo["fonteOficial"] = "Cartão oficial (teste)";
            corpo["requisitos"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(requisitos, System.Text.Json.JsonSerializerOptions.Web));
            return await Esperar(await Admin.PostAsJsonAsync("/api/RevisoesRequisitos", corpo), esperado);
        }

        await CriarAsync(new { especialidadeId = Guid.NewGuid() }, HttpStatusCode.NotFound, Item("1"));
        var inativa = Id(await EspecialidadeAsync(area));
        await Esperar(await AlterarSituacaoAsync(inativa, ativar: false, Versao(await ObterEspecialidadeAsync(inativa))));
        Assert.Equal("Especialidade inativa.", (string)(await CriarAsync(new { especialidadeId = inativa }, HttpStatusCode.Conflict, Item("1")))["title"]!);
        var exigenciaInvalida = await CriarAsync(new { classe = "Amigo" }, HttpStatusCode.BadRequest,
            Item("1", new { quantidade = 1, especialidadeId = inativa }));
        Assert.NotNull(exigenciaInvalida["errors"]!["Requisitos"]);

        await Esperar(await Admin.PostAsJsonAsync($"/api/RevisoesRequisitos/{Guid.NewGuid()}/Publicar", new { versao = "AAAAAAAAAAA=" }), HttpStatusCode.NotFound);

        var alvo = Id(await EspecialidadeAsync(area));
        var rascunho = await CriarAsync(new { especialidadeId = alvo }, HttpStatusCode.Created, Item("1"));
        var (desbravador, _) = await MembroAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await desbravador.GetAsync($"/api/RevisoesRequisitos/{Id(rascunho)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetAsync($"/api/RevisoesRequisitos/{Id(rascunho)}")).StatusCode);
        var descartada = await Esperar(await Admin.PostAsJsonAsync($"/api/RevisoesRequisitos/{Id(rascunho)}/Descartar",
            new { motivo = "rascunho abandonado", versao = Versao(rascunho) }));
        Assert.Equal(HttpStatusCode.NotFound, (await desbravador.GetAsync($"/api/RevisoesRequisitos/{Id(descartada)}")).StatusCode);
    }

    private Task<HttpResponseMessage> VincularEvidenciaAsync(Guid classe, Guid requisito, Guid especialidade) =>
        Admin.PostAsJsonAsync($"/api/Formacao/Classes/{classe}/Requisitos/{requisito}/Evidencias", new { membroEspecialidadeId = especialidade });

    [Fact]
    public async Task Evidencias_RemocaoEMigracaoDeRevisao_DesativamAEvidencia_ERequisitoSemExigenciaRecusa()
    {
        var area = AreaUnica();
        var (_, membro) = await MembroAsync(nascimento: Hoje.AddYears(-12));
        var (_, primeira) = await EspecialidadeConcluidaAsync(membro, area, Hoje.AddDays(-1));
        var (_, segunda) = await EspecialidadeConcluidaAsync(membro, area, Hoje.AddDays(-1));
        var exigencia = new { quantidade = 1, area };
        await PublicarAsync(new { classe = "Companheiro" }, Item("1", exigencia), Item("2"));
        var classe = await AssociarClasseAsync(membro, "Companheiro", Hoje.AddDays(-10));
        var id = Id(classe);

        var semExigencia = await Esperar(await VincularEvidenciaAsync(id, Req(classe, "2"), primeira), HttpStatusCode.Conflict);
        Assert.Equal("O requisito não exige especialidades.", (string)semExigencia["title"]!);

        await Esperar(await VincularEvidenciaAsync(id, Req(classe, "1"), primeira));
        await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Formacao/Classes/{id}/Requisitos/{Req(classe, "1")}/Evidencias/{primeira}")
        {
            Content = JsonContent.Create(new { justificativa = "vinculada por engano" }),
        }));
        var removida = await Db(db => db.EvidenciasEspecialidades.AsNoTracking().SingleAsync(e => e.MembroClasseId == id && e.MembroEspecialidadeId == primeira));
        Assert.False(removida.Ativo);
        Assert.NotNull(removida.RemovidaPorUsuarioId);
        Assert.NotNull(removida.RemovidaEmUtc);

        await Esperar(await VincularEvidenciaAsync(id, Req(classe, "1"), segunda));
        var nova = await PublicarAsync(new { classe = "Companheiro" }, Item("1", exigencia), Item("2"), Item("3"));
        await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/Classes/{id}/MigrarRevisao",
            new { revisaoId = Id(nova), justificativa = "nova edição do cartão", versao = Versao(await ObterAsync("Classes", id)) }));
        Assert.False((await Db(db => db.EvidenciasEspecialidades.AsNoTracking()
            .SingleAsync(e => e.MembroClasseId == id && e.MembroEspecialidadeId == segunda && e.RemovidaEmUtc != null))).Ativo);

        var historico = await Db(db => db.HistoricoFormacao.AsNoTracking().Where(h => h.VinculoId == id).ToListAsync());
        Assert.Contains(historico, h => h.Acao == "MIGRAR_REVISAO" && h.Motivo == "nova edição do cartão");
        Assert.All(historico, h => Assert.Equal(FormacaoSqlFixture.Ip, h.IpResponsavel));
    }

    [Fact]
    public async Task Catalogo_ClasseForaDoCatalogo_RevisaoDescartadaSoParaAGestao_EMembroSemFormacao()
    {
        // Pela rota, o model binding já recusa o valor fora do enum (400); o serviço tem o próprio guarda para quem o chama direto.
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/Classes/99")).StatusCode);
        using (var escopo = fx.Factory.Services.CreateScope())
        {
            var catalogo = escopo.ServiceProvider.GetRequiredService<Almirante.Api.Services.CatalogoFormacaoService>();
            Assert.Null(await catalogo.ObterClasseAsync((ClasseDesbravador)99, default));
            Assert.Null(await catalogo.ListarRevisoesAsync(new Almirante.Api.Dtos.ListarRevisoesQuery((ClasseDesbravador)99, null, true), default));
        }

        var especialidade = await EspecialidadeAsync(AreaUnica());
        var rascunho = await Esperar(await Admin.PostAsJsonAsync("/api/RevisoesRequisitos", new
        {
            especialidadeId = Id(especialidade), edicao = "descartada", fonteOficial = "f", requisitos = new[] { Item("1") },
        }), HttpStatusCode.Created);
        await Esperar(await Admin.PostAsJsonAsync($"/api/RevisoesRequisitos/{Id(rascunho)}/Descartar", new { motivo = "Edição errada", versao = Versao(rascunho) }));
        var (participante, membro) = await MembroAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await participante.GetAsync($"/api/RevisoesRequisitos/{Id(rascunho)}")).StatusCode);
        Assert.Equal("Descartada", (string)(await Esperar(await Admin.GetAsync($"/api/RevisoesRequisitos/{Id(rascunho)}")))["situacao"]!);

        var semFormacao = await Esperar(await Admin.GetAsync($"/api/Formacao/Membros/{membro}"));
        Assert.Empty(semFormacao["classes"]!.AsArray());
        Assert.Empty(semFormacao["especialidades"]!.AsArray());
    }

    [Fact]
    public async Task Gravacao_QuePerdeACorridaDeRowversion_Responde409SemGravar_EHistoricoApontaOVinculoDono()
    {
        var especialidade = await EspecialidadeAsync(AreaUnica());
        await PublicarAsync(new { especialidadeId = Id(especialidade) }, Item("1"));
        var (_, membro) = await MembroAsync();

        fx.Falhas.FalharProximoSave(() => new DbUpdateConcurrencyException("outra operação gravou antes (simulado)"));
        var conflito = await AssociarEspecialidadeAsync(membro, Id(especialidade), esperado: HttpStatusCode.Conflict);
        Assert.Equal("Conflito de concorrência.", (string)conflito["title"]!);
        Assert.False(await Db(db => db.MembrosEspecialidades.AnyAsync(v => v.MembroId == membro)));

        var vinculo = Id(await AssociarEspecialidadeAsync(membro, Id(especialidade)));
        await Esperar(await RequisitoAsync("Especialidades", vinculo, Req(await ObterAsync("Especialidades", vinculo), "1"), "Validado"));
        var progresso = await Db(db => db.ProgressoRequisitos.Where(p => p.MembroEspecialidadeId == vinculo).Select(p => p.Id).SingleAsync());
        var linhas = await Db(db => db.HistoricoFormacao.AsNoTracking().Where(h => h.RegistroId == vinculo || h.RegistroId == progresso).ToListAsync());
        Assert.Contains(linhas, h => h.RegistroId == progresso);
        Assert.All(linhas, h => Assert.Equal(vinculo, h.VinculoId));

        Assert.Equal([vinculo], (await ItensAsync($"/api/Formacao/Especialidades?membroId={membro}&aguardandoInvestiduraOuEntrega=false")).Select(Id));
        Assert.Empty(await ItensAsync($"/api/Formacao/Especialidades?membroId={membro}&aguardandoInvestiduraOuEntrega=true"));
    }

    [Fact]
    public async Task RelatorioDeClasses_FiltradoPorGrupoOuPorClasse()
    {
        var lideranca = (await Esperar(await Admin.GetAsync("/api/Formacao/Relatorios/Participantes?tipo=Classe&grupo=Lideranca")))["items"]!.AsArray();
        Assert.Equal(["Lider", "LiderMaster", "LiderMasterAvancado"], lideranca.Select(l => (string)l!["classe"]!["codigo"]!));

        var amigo = (await Esperar(await Admin.GetAsync("/api/Formacao/Relatorios/Participantes?tipo=Classe&classe=Amigo")))["items"]!.AsArray();
        Assert.Equal("Amigo", (string)Assert.Single(amigo)!["classe"]!["codigo"]!);
    }

    [Fact]
    public async Task Operacoes_EmVinculoInexistente_Respondem404PorTipo()
    {
        foreach (var recurso in new[] { "Classes", "Especialidades" })
        {
            var resposta = await Esperar(await Admin.PostAsJsonAsync($"/api/Formacao/{recurso}/{Guid.NewGuid()}/Cancelar",
                new { justificativa = "teste", versao = "AAAAAAAAAAA=" }), HttpStatusCode.NotFound);
            Assert.Equal(recurso == "Classes" ? "Vínculo de classe não encontrado." : "Vínculo de especialidade não encontrado.", (string)resposta["title"]!);
        }
    }
}
