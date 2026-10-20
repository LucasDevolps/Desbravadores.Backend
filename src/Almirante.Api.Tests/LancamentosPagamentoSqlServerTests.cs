using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class LancamentosPagamentoSqlServerTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SqlServerApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static DateOnly Ontem => TestHelpers.DataPagamentoPassada;

    public async Task InitializeAsync()
    {
        _factory = new SqlServerApiFactory();
        _client = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<LancamentoDto> CriarAsync(decimal valor, string tipoFluxo = "Entrada")
    {
        var membro = await TestHelpers.AddUsuarioAsync(_factory, $"Pagamento SQL {Guid.NewGuid():N}", "DS");
        var criado = await _client.PostAsJsonAsync("/api/Lancamentos/Registrar", new
        {
            membroId = membro.Id, finalidade = "Mensalidade", categoria = "Clube", tipoFluxo, valor,
            vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = false,
        });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        return (await criado.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
    }

    private Task<Lancamento> LerAsync(Guid id) =>
        TestHelpers.WithDbAsync(_factory, db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == id));

    private async Task<int> ExecAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new SqlConnection(_factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        return await comando.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ConfirmacoesSimultaneas_SomenteUmaVence_AsDemaisRecebem409_SemErro()
    {
        var lancamento = await CriarAsync(100m);

        var respostas = await Task.WhenAll(Enumerable.Range(1, 10).Select(i =>
            TestHelpers.RegistrarPagamentoAsync(_client, lancamento.Id, 90m + i, Ontem)));

        Assert.Single(respostas, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var vencedor = (await respostas.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        Assert.Equal(vencedor.ValorPago, (await LerAsync(lancamento.Id)).ValorPago);
    }

    [Fact]
    public async Task ConfirmacoesSimultaneas_EmLancamentoDeEvento_SomenteUmaVence()
    {
        var evento = await EventosSqlSupport.CriarAsync(_client, await EventosSqlSupport.MembrosAsync(_factory, 1));
        var alvo = evento.Lancamentos[0];

        var respostas = await Task.WhenAll(Enumerable.Range(1, 8).Select(_ =>
            TestHelpers.RegistrarPagamentoAsync(_client, alvo.Id, alvo.Valor, Ontem)));

        Assert.Single(respostas, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(respostas.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var salvo = await LerAsync(alvo.Id);
        Assert.Equal((StatusLancamento.Pago, alvo.Valor, Ontem), (salvo.Status, salvo.ValorPago!.Value, salvo.DataPagamento!.Value));
    }

    [Fact]
    public async Task PagamentoEReducaoDeValorSimultaneos_NuncaDeixamEstadoInconsistente()
    {
        for (var rodada = 0; rodada < 6; rodada++)
        {
            var lancamento = await CriarAsync(50m);

            var pagar = TestHelpers.RegistrarPagamentoAsync(_client, lancamento.Id, 50m, Ontem);
            var reduzir = _client.PutAsJsonAsync($"/api/Lancamentos/{lancamento.Id}", new { valor = 10m });
            await Task.WhenAll(pagar, reduzir);

            var (pagou, reduziu) = ((await pagar).StatusCode, (await reduzir).StatusCode);
            Assert.DoesNotContain(HttpStatusCode.InternalServerError, new[] { pagou, reduziu });
            var salvo = await LerAsync(lancamento.Id);
            if (pagou == HttpStatusCode.OK)
            {
                Assert.Equal((50m, 50m, StatusLancamento.Pago), (salvo.Valor, salvo.ValorPago!.Value, salvo.Status));
                Assert.Equal(HttpStatusCode.Conflict, reduziu);
            }
            else
            {
                Assert.Contains(pagou, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict });
                Assert.Equal((10m, (decimal?)null, StatusLancamento.Pendente), (salvo.Valor, salvo.ValorPago, salvo.Status));
            }
        }
    }

    [Theory]
    [InlineData("UPDATE dbo.Lancamentos SET DataPagamento = '2026-01-10', ValorPago = 10, PagamentoRegistradoPorUsuarioId = MembroId, PagamentoRegistradoEmUtc = SYSUTCDATETIME() WHERE Id = @id")]
    [InlineData("UPDATE dbo.Lancamentos SET Status = 1, DataPagamento = '2026-01-10', ValorPago = 60, PagamentoRegistradoPorUsuarioId = MembroId, PagamentoRegistradoEmUtc = SYSUTCDATETIME() WHERE Id = @id")]
    [InlineData("UPDATE dbo.Lancamentos SET Status = 1, DataPagamento = '2026-01-10' WHERE Id = @id")]
    [InlineData("UPDATE dbo.Lancamentos SET Status = 1, DataPagamento = '2026-01-10', ValorPago = 0, PagamentoRegistradoPorUsuarioId = MembroId, PagamentoRegistradoEmUtc = SYSUTCDATETIME() WHERE Id = @id")]
    [InlineData("UPDATE dbo.Lancamentos SET Status = 1, DataPagamento = '2026-01-10', ValorPago = 10 WHERE Id = @id")]
    public async Task CheckConstraint_RecusaPagamentoInconsistenteGravadoDiretoNoBanco(string sql)
    {
        var lancamento = await CriarAsync(50m);

        var erro = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(sql, ("@id", lancamento.Id)));

        Assert.Equal(547, erro.Number);
        Assert.Contains("CK_Lancamentos_Pagamento", erro.Message);
    }

    [Fact]
    public async Task CheckConstraint_AceitaPagoHistoricoSemDadosDePagamento()
    {
        var lancamento = await CriarAsync(50m);

        Assert.Equal(1, await ExecAsync("UPDATE dbo.Lancamentos SET Status = 1 WHERE Id = @id", ("@id", lancamento.Id)));
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, lancamento.Id, 50m, Ontem.AddDays(-60))).StatusCode);
    }

    [Fact]
    public async Task ExclusaoLogica_DeLancamentoPago_CopiaOsDadosDePagamentoParaAAuditoria()
    {
        var lancamento = await CriarAsync(50m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, lancamento.Id, 47.5m, Ontem)).StatusCode);
        var pago = await LerAsync(lancamento.Id);

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{lancamento.Id}") { Content = JsonContent.Create(new { motivo = "estorno" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);

        var auditoria = await TestHelpers.WithDbAsync(_factory, db => db.LancamentosDeletados.AsNoTracking().SingleAsync(d => d.LancamentoId == lancamento.Id));
        Assert.Equal((StatusLancamento.Pago, Ontem, 47.5m, pago.PagamentoRegistradoPorUsuarioId, pago.PagamentoRegistradoEmUtc, "estorno"),
            (auditoria.Status, auditoria.DataPagamento!.Value, auditoria.ValorPago!.Value, auditoria.PagamentoRegistradoPorUsuarioId, auditoria.PagamentoRegistradoEmUtc, auditoria.Motivo));
    }

    [Fact]
    public async Task PagamentoDeEvento_GravaDados_EOEventoContinuaProtegido()
    {
        var membros = await EventosSqlSupport.MembrosAsync(_factory, 2);
        var evento = await EventosSqlSupport.CriarAsync(_client, membros);
        var alvo = evento.Lancamentos[0];

        var resposta = await TestHelpers.RegistrarPagamentoAsync(_client, alvo.Id, alvo.Valor - 1m, Ontem);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var atual = (await _client.GetFromJsonAsync<EventoDto>($"/api/Eventos/{evento.Id}", Json))!;
        Assert.NotEqual(evento.Versao, atual.Versao);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.PutAsync(_client, evento.Id, EventosSqlSupport.Editar(atual, membros, transporte: 99m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EventosTestKit.DeleteAsync(_client, evento.Id, "teste", atual.Versao)).StatusCode);

        var local = await EventosTestKit.PutAsync(_client, evento.Id, EventosSqlSupport.Editar(atual, membros, local: "Novo local"));
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        var salvo = await LerAsync(alvo.Id);
        Assert.Equal((StatusLancamento.Pago, alvo.Valor, alvo.Valor - 1m, Ontem), (salvo.Status, salvo.Valor, salvo.ValorPago!.Value, salvo.DataPagamento!.Value));
        Assert.Contains("Novo local", salvo.Descricao);

        var corrigido = await _client.PutAsJsonAsync($"/api/Lancamentos/{alvo.Id}/Pagamento", new { dataPagamento = Ontem.AddDays(-1).ToString("yyyy-MM-dd"), valorPago = alvo.Valor });
        Assert.Equal(HttpStatusCode.OK, corrigido.StatusCode);
        Assert.Equal(alvo.Valor, (await LerAsync(alvo.Id)).ValorPago);
    }

    [Fact]
    public async Task PagamentoDeEventoExcluido_Retorna404()
    {
        var evento = await EventosSqlSupport.CriarAsync(_client, await EventosSqlSupport.MembrosAsync(_factory, 1));
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(_client, evento.Id, "cancelado", evento.Versao)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await TestHelpers.RegistrarPagamentoAsync(_client, evento.Lancamentos[0].Id, 1m, Ontem)).StatusCode);
    }

    [Fact]
    public async Task Dashboard_AgregaNoBanco_RespeitaRegras_EAgrupaPasseios()
    {
        var hojeCivil = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);
        var entrada = await CriarAsync(100m);
        var entradaParcial = await CriarAsync(80m);
        var saida = await CriarAsync(33.33m, "Saida");
        var pendente = await CriarAsync(999m);
        var excluida = await CriarAsync(70m);
        var foraDoPeriodo = await CriarAsync(500m);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, entrada.Id, 100m, Ontem)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, entradaParcial.Id, 79.99m, Ontem)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, saida.Id, 33.33m, Ontem)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, excluida.Id, 70m, Ontem)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(_client, foraDoPeriodo.Id, 500m, Ontem.AddDays(-1))).StatusCode);
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{excluida.Id}") { Content = JsonContent.Create(new { motivo = "duplicado" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);
        Assert.Equal(1, await ExecAsync("UPDATE dbo.Lancamentos SET Status = 1 WHERE Id = @id", ("@id", pendente.Id)));

        var membros = await EventosSqlSupport.MembrosAsync(_factory, 5);
        var data = hojeCivil.AddDays(9);
        var raiz = await EventosSqlSupport.CriarAsync(_client, membros.Take(2).ToList(), data: data, local: "Campori");
        var grupo = await EventosSqlSupport.CriarAsync(_client, membros.Skip(2).Take(1).ToList(), referencia: raiz.Id, data: data, local: "Campori");
        var passado = await EventosSqlSupport.CriarAsync(_client, membros.Skip(3).Take(1).ToList(), data: hojeCivil.AddDays(4), local: "Passado");
        Assert.Equal(1, await ExecAsync("UPDATE dbo.eventos SET DataEvento = @d WHERE Id = @id", ("@d", hojeCivil.AddDays(-1).ToDateTime(TimeOnly.MinValue)), ("@id", passado.Id)));
        var cancelado = await EventosSqlSupport.CriarAsync(_client, membros.Skip(4).Take(1).ToList(), data: hojeCivil.AddDays(2), local: "Cancelado");
        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(_client, cancelado.Id, "cancelado", cancelado.Versao)).StatusCode);
        var inativo = await TestHelpers.AddUsuarioAsync(_factory, "Desbravador que saiu", "DS");
        var desbravadoresAtivos = await TestHelpers.WithDbAsync(_factory, db => db.Usuarios.CountAsync(u => u.Ativo && u.Cargo!.Role == "DS"));
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/Usuarios/{inativo.Id}")).StatusCode);

        var resposta = await _client.GetAsync($"/api/Dashboard?dataInicial={Ontem:yyyy-MM-dd}&dataFinal={Ontem:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var dashboard = (await resposta.Content.ReadFromJsonAsync<DashboardResponse>(Json))!;
        Assert.Equal(new DashboardFinanceiroDto(146.66m, 179.99m, 33.33m), dashboard.Financeiro);
        Assert.Equal(desbravadoresAtivos - 1, dashboard.Desbravadores.QuantidadeAtivos);
        var passeio = Assert.Single(dashboard.ProximosEventos);
        Assert.Equal((raiz.EventoGrupoId, data, "Campori", 3), (passeio.EventoGrupoId, passeio.Data, passeio.Local, passeio.QuantidadeParticipantes));
        Assert.Equal(new[] { raiz.Id, grupo.Id }.Order(), passeio.EventoIds);
    }
}
