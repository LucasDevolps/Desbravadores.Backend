using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

/// <summary>Issue #160: regras de atendimento e corridas de idempotência do almoxarifado que a auditoria encontrou sem cenário.</summary>
public sealed partial class AlmoxarifadoSqlServerTests
{
    private static Exception ViolacaoDeUnicidade() => new DbUpdateException("simulada", SqlExceptionDeTeste.Criar(2601));

    [Fact]
    public async Task Atendimento_RecusaBemBaixadoOuExcluido_EBemAdquiridoDepoisDaData()
    {
        var categoria = Categoria();
        var recebedor = await MembroAsync("SEC");
        var desejo = Id(await DesejoAsync(categoria, quantidade: 3));
        var baixado = Id(await BemAsync(categoria));
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{baixado}/Baixa",
            new { dataBaixa = D(Hoje), motivo = "Descarte", justificativa = "Rasgado" }));
        var excluido = await BemAsync(categoria);
        await Esperar(await Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/Almoxarifado/Bens/{Id(excluido)}")
        {
            Content = JsonContent.Create(new { justificativa = "cadastro duplicado", versao = Versao(excluido) }),
        }));
        var recente = BemCorpo(categoria);
        recente["dataAquisicao"] = D(Hoje);
        var novo = Id(await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", recente), HttpStatusCode.Created));

        foreach (var bem in new[] { baixado, Id(excluido) })
        {
            await Status(await AtenderAsync(desejo, new { quantidade = 1, data = D(Hoje), responsavelId = recebedor, origem = "Doacao", bensExistentes = new[] { bem } }),
                HttpStatusCode.Conflict, "Bem excluído ou baixado.");
        }
        await Status(await AtenderAsync(desejo, new { quantidade = 1, data = D(Hoje.AddDays(-5)), responsavelId = recebedor, origem = "Doacao", bensExistentes = new[] { novo } }),
            HttpStatusCode.BadRequest, "foi adquirido depois da data do atendimento");
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM dbo.almoxarifado_atendimentos WHERE DesejoId = @d", ("@d", desejo)));
    }

    [Fact]
    public async Task Atendimento_CorridaPelaMesmaChave_Responde409SemGravarNada()
    {
        var categoria = Categoria();
        var recebedor = await MembroAsync("SEC");
        var desejo = Id(await DesejoAsync(categoria, quantidade: 3));
        var outroDesejo = Id(await DesejoAsync(categoria, quantidade: 3));
        var admin = await Db(db => db.Usuarios.Where(u => u.EmailNormalizado == "ADMIN@LOCAL.DEV").Select(u => u.Id).SingleAsync());
        object Corpo() => new { quantidade = 1, data = D(Hoje), responsavelId = recebedor, origem = "Doacao", bensNovos = new[] { NovoBem("Lona", categoria) } };

        // Sem operação gravada por outro: a violação simultânea é devolvida como conflito.
        fx.Falhas.FalharProximoSave(ViolacaoDeUnicidade);
        await Status(await AtenderAsync(desejo, Corpo()), HttpStatusCode.Conflict, "Conflito de operação simultânea.");

        // A outra operação gravou a mesma chave (com outro conteúdo) no instante do conflito: vale a regra da idempotência.
        var chave = Guid.NewGuid();
        fx.Falhas.FalharProximoSave(ViolacaoDeUnicidade, async () =>
        {
            await using var conexao = new SqlConnection(fx.Factory.ConnectionString);
            await conexao.OpenAsync();
            await using var comando = new SqlCommand("""
                EXEC sys.sp_set_session_context @key=N'UsuarioResponsavelId', @value=@admin;
                EXEC sys.sp_set_session_context @key=N'IpResponsavelExclusao', @value='127.0.0.1';
                INSERT INTO dbo.almoxarifado_atendimentos (Id, DesejoId, Quantidade, Data, ResponsavelId, Origem, IdempotencyKey, RequestHash,
                    RegistradoPorUsuarioId, RegistradoEmUtc)
                VALUES (NEWID(), @outro, 1, @data, @responsavel, 2, @chave, REPLICATE('0', 64), @admin, SYSUTCDATETIME());
                """, conexao);
            comando.Parameters.AddWithValue("@admin", admin);
            comando.Parameters.AddWithValue("@outro", outroDesejo);
            comando.Parameters.AddWithValue("@data", Hoje.ToDateTime(TimeOnly.MinValue));
            comando.Parameters.AddWithValue("@responsavel", recebedor);
            comando.Parameters.AddWithValue("@chave", chave.ToString());
            await comando.ExecuteNonQueryAsync();
        });
        await Status(await AtenderAsync(desejo, Corpo(), chave), HttpStatusCode.Conflict, "Chave de idempotência já usada com dados diferentes.");

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM dbo.almoxarifado_atendimentos WHERE DesejoId = @d", ("@d", desejo)));
        Assert.Equal(0, (int)(await Esperar(await Admin.GetAsync($"/api/Almoxarifado/Desejos/{desejo}")))["quantidadeAtendida"]!);
    }

    [Fact]
    public async Task Baixa_DepoisDeManutencaoEEmprestimo_ConfereAMovimentacaoMaisRecente()
    {
        var bem = Id(await BemAsync(Categoria()));
        var manutencao = await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Manutencoes",
            new { dataEntrada = D(Hoje.AddDays(-10)), motivo = "Costura" }), HttpStatusCode.Created);
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Manutencoes/{Id(manutencao)}/Saida",
            new { dataSaida = D(Hoje.AddDays(-9)), conservacaoSaida = "Bom" }));
        var emprestimo = Id(await Esperar(await EmprestarAsync(bem, await MembroAsync(), Hoje.AddDays(-5), Hoje.AddDays(-4)), HttpStatusCode.Created));
        await Esperar(await DevolverAsync(emprestimo, Hoje.AddDays(-3)));

        await Status(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Baixa",
            new { dataBaixa = D(Hoje.AddDays(-4)), motivo = "Descarte", justificativa = "Rasgado" }), HttpStatusCode.BadRequest);
        await Esperar(await Admin.PostAsJsonAsync($"/api/Almoxarifado/Bens/{bem}/Baixa", new { dataBaixa = D(Hoje), motivo = "Descarte", justificativa = "Rasgado" }));
    }

    [Fact]
    public async Task Atendimento_ComBemDeDataDeAquisicaoDesconhecida_EAceito()
    {
        var categoria = Categoria();
        var recebedor = await MembroAsync("SEC");
        var desejo = Id(await DesejoAsync(categoria, quantidade: 1));
        var acervo = BemCorpo(categoria, nome: "Lampião antigo");
        acervo["dataAquisicao"] = null;
        acervo["dataAquisicaoDesconhecida"] = true;
        acervo["justificativaDataDesconhecida"] = "Doação antiga sem registro";
        var bem = Id(await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", acervo), HttpStatusCode.Created));

        await Esperar(await AtenderAsync(desejo, new { quantidade = 1, data = D(Hoje), responsavelId = recebedor, origem = "Doacao", bensExistentes = new[] { bem } }),
            HttpStatusCode.Created);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM dbo.almoxarifado_atendimentos WHERE DesejoId = @d", ("@d", desejo)));
    }

    [Fact]
    public async Task Bem_TrocaDeGuardaConfereOMembro_CsvTrazGuardaEDataDesconhecida_EEmprestimoEmDiaNaoEhAtrasado()
    {
        var categoria = Categoria();
        var guarda = await MembroAsync("SEC");
        var bem = await BemAsync(categoria);
        var corpo = BemCorpo(categoria);
        corpo["versao"] = Versao(bem);
        corpo["responsavelGuardaId"] = Guid.NewGuid();
        await Status(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{Id(bem)}", corpo), HttpStatusCode.BadRequest, "deve ser um membro ativo");
        corpo["responsavelGuardaId"] = guarda;
        var atualizado = await Esperar(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{Id(bem)}", corpo));
        Assert.Equal(guarda, Id(atualizado["responsavelGuarda"]!));
        corpo["versao"] = Versao(atualizado);
        var mantido = await Esperar(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{Id(bem)}", corpo));
        var outraGuarda = await MembroAsync("TES");
        corpo["responsavelGuardaId"] = outraGuarda;
        corpo["versao"] = Versao(mantido);
        Assert.Equal(outraGuarda, Id((await Esperar(await Admin.PutAsJsonAsync($"/api/Almoxarifado/Bens/{Id(bem)}", corpo)))["responsavelGuarda"]!));

        var acervo = BemCorpo(categoria, nome: "Lampião antigo");
        acervo["dataAquisicao"] = null;
        acervo["dataAquisicaoDesconhecida"] = true;
        acervo["justificativaDataDesconhecida"] = "Doação antiga sem registro";
        await Esperar(await Admin.PostAsJsonAsync("/api/Almoxarifado/Bens", acervo), HttpStatusCode.Created);
        var csv = await (await Admin.GetAsync($"/api/Almoxarifado/Relatorios/Patrimonio/Csv?categoria={categoria}")).Content.ReadAsStringAsync();
        Assert.Contains("Membro TES", csv);
        Assert.Contains("Desconhecida", csv);

        await Esperar(await EmprestarAsync(Id(bem), await MembroAsync()), HttpStatusCode.Created);
        Assert.Single(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos?bemId={Id(bem)}&atrasados=false")));
        Assert.Empty(await Lista(await Admin.GetAsync($"/api/Almoxarifado/Emprestimos?bemId={Id(bem)}&atrasados=true")));
    }
}
