using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

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
}
