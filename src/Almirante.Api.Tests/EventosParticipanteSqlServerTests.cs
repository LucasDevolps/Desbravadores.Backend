using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Almirante.Api.Tests.NotificacoesTestKit;

namespace Almirante.Api.Tests;

/// <summary>
/// Eventos operados por Secretário e Tesoureiro (GestaoEventos) e consultados pelo participante em /api/Me/Eventos, contra SQL Server real:
/// geração de cobranças, idempotência, notificação, pagamento, remoção, exclusão e eventos passados.
/// </summary>
[Trait("Category", "RequiresSqlServer")]
public sealed class EventosParticipanteSqlServerTests(EventosSqlFixture fx) : IClassFixture<EventosSqlFixture>
{
    private SqlServerApiFactory Factory => fx.Factory;

    private Task<(HttpClient Client, Guid Id)> UsuarioAsync(string role) => TestHelpers.CreateAuthenticatedClientForRoleAsync(Factory, role);

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private static async Task<List<Guid>> MeusEventosAsync(HttpClient client)
    {
        var resposta = await client.GetAsync("/api/Me/Eventos");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await JsonAsync(resposta))["items"]!.AsArray().Select(i => (Guid)i!["id"]!).ToList();
    }

    private async Task MoverEventoParaAsync(Guid eventoId, DateOnly data)
    {
        await using var conexao = new SqlConnection(Factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "UPDATE dbo.eventos SET DataEvento = @d WHERE Id = @id";
        comando.Parameters.AddWithValue("@d", data.ToDateTime(TimeOnly.MinValue));
        comando.Parameters.AddWithValue("@id", eventoId);
        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task SecretarioCriaEvento_NotificacaoLevaAoDetalheDoParticipante_SemVazarOutrosNemDadosAdministrativos()
    {
        var (secretario, secretarioId) = await UsuarioAsync("SEC");
        var (participante, participanteId) = await UsuarioAsync("DS");
        var (_, outroParticipanteId) = await UsuarioAsync("DS");
        var (naoParticipante, _) = await UsuarioAsync("DS");
        var chave = Guid.NewGuid().ToString();

        var evento = await EventosSqlSupport.CriarAsync(secretario, new[] { participanteId, outroParticipanteId }, chave: chave);
        var reenvio = await EventosSqlSupport.CriarAsync(secretario, new[] { participanteId, outroParticipanteId }, chave: chave, esperado: HttpStatusCode.OK);
        Assert.Equal((evento.Id, 2, 20m), (reenvio.Id, evento.Lancamentos.Count, evento.ValorPorMembro));
        Assert.Equal(secretarioId, await TestHelpers.WithDbAsync(Factory, db => db.Eventos.Where(e => e.Id == evento.Id).Select(e => e.CriadoPorUsuarioId).SingleAsync()));

        var notificacao = Assert.Single((await PendentesAsync(participante)).Items);
        Assert.Equal((TipoNotificacao.EventoCadastrado, evento.Id), (notificacao.Tipo, notificacao.EventoId));

        var resposta = await participante.GetAsync($"/api/Me/Eventos/{notificacao.EventoId}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(texto)!.AsObject();
        Assert.Equal(["id", "dataEvento", "local", "transporte", "alimentacao", "seguroObrigatorio", "valorPorMembro", "minhaCobranca"], json.Select(p => p.Key));
        Assert.Equal((evento.Id, evento.DataEvento.ToString("yyyy-MM-dd"), "Parque Ibirapuera", 20m, 10m, 8m, 2m),
            ((Guid)json["id"]!, (string)json["dataEvento"]!, (string)json["local"]!, (decimal)json["valorPorMembro"]!,
             (decimal)json["transporte"]!["valor"]!, (decimal)json["alimentacao"]!["valor"]!, (decimal)json["seguroObrigatorio"]!));
        Assert.Equal((20m, evento.DataEvento.ToString("yyyy-MM-dd"), "Pendente"),
            ((decimal)json["minhaCobranca"]!["valor"]!, (string)json["minhaCobranca"]!["vencimento"]!, (string)json["minhaCobranca"]!["status"]!));
        Assert.DoesNotContain(outroParticipanteId.ToString(), texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(evento.Lancamentos.Single(l => l.MembroId == outroParticipanteId).Id.ToString(), texto, StringComparison.OrdinalIgnoreCase);
        foreach (var administrativo in new[] { "\"membros\"", "\"lancamentos\"", "\"total\"", "\"quantidadeMembros\"", "\"versao\"", "\"eventoGrupoId\"", "\"eventoReferenciaId\"", "\"ativo\"", evento.Versao })
        {
            Assert.DoesNotContain(administrativo, texto);
        }
        Assert.Equal([evento.Id], await MeusEventosAsync(participante));

        var alheio = await naoParticipante.GetAsync($"/api/Me/Eventos/{evento.Id}");
        var inexistente = await naoParticipante.GetAsync($"/api/Me/Eventos/{Guid.NewGuid()}");
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (alheio.StatusCode, inexistente.StatusCode));
        Assert.Equal(await TestHelpers.NormalizedProblemAsync(inexistente), await TestHelpers.NormalizedProblemAsync(alheio));
        Assert.Empty(await MeusEventosAsync(naoParticipante));
        Assert.Equal(HttpStatusCode.Forbidden, (await participante.GetAsync($"/api/Eventos/{evento.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EventosTestKit.PutAsync(participante, evento.Id, EventosSqlSupport.Editar(evento, participanteId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EventosTestKit.DeleteAsync(participante, evento.Id, "x", evento.Versao)).StatusCode);

        var leitura = await LerAsync(participante, notificacao.Id);
        Assert.Equal(HttpStatusCode.OK, leitura.StatusCode);
        Assert.Equal((0, 0), ((await PendentesAsync(participante)).Items.Count, (await PendentesAsync(participante)).NaoLidas));
        Assert.NotNull(await TestHelpers.WithDbAsync(Factory, db => db.Notificacoes.Where(n => n.Id == notificacao.Id).Select(n => n.LidaEmUtc).SingleAsync()));
        Assert.Equal(HttpStatusCode.OK, (await participante.GetAsync($"/api/Me/Eventos/{notificacao.EventoId}")).StatusCode);
    }

    [Fact]
    public async Task TesoureiroOperaEventoECobranca_SecretarioNaoPaga_EParticipanteVeOStatusDaPropriaCobranca()
    {
        var (secretario, _) = await UsuarioAsync("SEC");
        var (tesoureiro, tesoureiroId) = await UsuarioAsync("TES");
        var (participante, participanteId) = await UsuarioAsync("DS");
        var evento = await EventosSqlSupport.CriarAsync(secretario, participanteId, local: "Sítio Teste");
        var lancamentoId = evento.Lancamentos.Single().Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await TestHelpers.RegistrarPagamentoAsync(secretario, lancamentoId, 20m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await secretario.GetAsync($"/api/Lancamentos/{lancamentoId}")).StatusCode);
        Assert.Equal(StatusLancamento.Pendente, await TestHelpers.WithDbAsync(Factory, db => db.Lancamentos.Where(l => l.Id == lancamentoId).Select(l => l.Status).SingleAsync()));

        var editado = await EventosTestKit.PutAsync(tesoureiro, evento.Id, EventosSqlSupport.Editar(evento, participanteId, local: "Sítio Teste II"));
        Assert.True(editado.StatusCode == HttpStatusCode.OK, await editado.Content.ReadAsStringAsync());
        Assert.Equal(tesoureiroId, await TestHelpers.WithDbAsync(Factory, db => db.Eventos.Where(e => e.Id == evento.Id).Select(e => e.AtualizadoPorUsuarioId).SingleAsync()));

        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.RegistrarPagamentoAsync(tesoureiro, lancamentoId, 20m)).StatusCode);
        var pagamento = await TestHelpers.WithDbAsync(Factory, db => db.Lancamentos.AsNoTracking().SingleAsync(l => l.Id == lancamentoId));
        Assert.Equal((StatusLancamento.Pago, 20m, tesoureiroId), (pagamento.Status, pagamento.ValorPago!.Value, pagamento.PagamentoRegistradoPorUsuarioId!.Value));

        var visto = await JsonAsync(await participante.GetAsync($"/api/Me/Eventos/{evento.Id}"));
        Assert.Equal(("Sítio Teste II", "Pago"), ((string)visto["local"]!, (string)visto["minhaCobranca"]!["status"]!));

        var custoComPagamento = await EventosTestKit.PutAsync(secretario, evento.Id,
            EventosSqlSupport.Editar(await EventosTestKit.LerAsync(editado), participanteId, transporte: 99m));
        Assert.Equal(HttpStatusCode.Conflict, custoComPagamento.StatusCode);
    }

    [Fact]
    public async Task ParticipanteRemovido_EEventoExcluido_404_MasEventoPassadoContinuaConsultavel()
    {
        var (secretario, _) = await UsuarioAsync("SEC");
        var (participante, participanteId) = await UsuarioAsync("DS");
        var (removido, removidoId) = await UsuarioAsync("DS");
        var passeio = await EventosSqlSupport.CriarAsync(secretario, new[] { participanteId, removidoId }, local: "Museu Teste");
        var excluido = await EventosSqlSupport.CriarAsync(secretario, participanteId, local: "Praia Teste");
        Assert.Equal([passeio.Id, excluido.Id], await MeusEventosAsync(participante));

        var semRemovido = await EventosTestKit.PutAsync(secretario, passeio.Id, EventosSqlSupport.Editar(passeio, participanteId, motivo: "Desistiu"));
        Assert.True(semRemovido.StatusCode == HttpStatusCode.OK, await semRemovido.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await removido.GetAsync($"/api/Me/Eventos/{passeio.Id}")).StatusCode);
        Assert.Empty(await MeusEventosAsync(removido));

        Assert.Equal(HttpStatusCode.NoContent, (await EventosTestKit.DeleteAsync(secretario, excluido.Id, "Cancelado", excluido.Versao)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await participante.GetAsync($"/api/Me/Eventos/{excluido.Id}")).StatusCode);

        var ontem = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40);
        await MoverEventoParaAsync(passeio.Id, ontem);
        var passado = await participante.GetAsync($"/api/Me/Eventos/{passeio.Id}");
        Assert.Equal(HttpStatusCode.OK, passado.StatusCode);
        Assert.Equal(ontem.ToString("yyyy-MM-dd"), (string)(await JsonAsync(passado))["dataEvento"]!);
        Assert.Equal([passeio.Id], await MeusEventosAsync(participante));
    }

    [Fact]
    public async Task ListaDoParticipante_EmOrdemDeData_SoComCadastrosEmQueEleEstaAtivo()
    {
        var (secretario, _) = await UsuarioAsync("SEC");
        var (participante, participanteId) = await UsuarioAsync("DS");
        var hoje = EventosTestKit.Hoje;
        var tarde = await EventosSqlSupport.CriarAsync(secretario, participanteId, data: hoje.AddDays(20), local: "Zoológico");
        var cedo = await EventosSqlSupport.CriarAsync(secretario, participanteId, data: hoje.AddDays(5), local: "Aquário");
        var outroGrupo = await EventosSqlSupport.CriarAsync(secretario, (await TestHelpers.AddUsuarioAsync(Factory, "Só no outro", "DS")).Id,
            data: hoje.AddDays(5), local: "Aquário", referencia: cedo.Id);

        var lista = await JsonAsync(await participante.GetAsync("/api/Me/Eventos"));

        Assert.Equal([cedo.Id, tarde.Id], lista["items"]!.AsArray().Select(i => (Guid)i!["id"]!));
        Assert.DoesNotContain(outroGrupo.Id.ToString(), lista.ToJsonString());
    }
}
