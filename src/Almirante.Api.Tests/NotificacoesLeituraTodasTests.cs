using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;
using static Almirante.Api.Tests.NotificacoesTestKit;

namespace Almirante.Api.Tests;

public sealed class NotificacoesLeituraTodasTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private Task<(HttpClient Client, Guid UsuarioId)> UsuarioAsync(string role = "DS") =>
        TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);

    private Task<List<Notificacao>> NotificacoesDeAsync(Guid destinatarioId) =>
        TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AsNoTracking()
            .Where(n => n.DestinatarioId == destinatarioId).OrderBy(n => n.Id).ToListAsync());

    private async Task<List<Notificacao>> SemearAsync(Guid destinatarioId, IEnumerable<DateTime> criadas)
    {
        var lista = criadas.Select((c, i) => new Notificacao
        {
            Id = Guid.NewGuid(), DestinatarioId = destinatarioId, Tipo = TipoNotificacao.DebitoCadastrado,
            Titulo = $"Teste {i}", Mensagem = $"Mensagem {i}", CriadaEmUtc = c, ChaveOrigem = $"teste:{Guid.NewGuid():N}",
        }).ToList();
        await TestHelpers.WithDbAsync(factory, async db => { db.Notificacoes.AddRange(lista); return await db.SaveChangesAsync(); });
        return lista;
    }

    private static IEnumerable<DateTime> Passado(int quantidade) =>
        Enumerable.Range(1, quantidade).Select(i => DateTime.UtcNow.AddMinutes(-i));

    private static object Estado(Notificacao n) => (n.Id, n.LidaEmUtc, n.AvisoPendente, n.AvisoTentativas, n.AvisoProximaTentativaEmUtc);

    [Fact]
    public async Task MaisPendenciasQueAMaiorPagina_UmaChamadaMarcaTodas_EAsNovasDepoisDoLimiteFicamPendentes()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        // Mais que a maior página de /Pendentes (50), não só que a padrão (20) usada pelo frontend.
        var pendentes = await SemearAsync(usuarioId, Passado(NotificacoesService.PageSizeMaximo + 10));
        var novaDepoisDoLimite = (await SemearAsync(usuarioId, [DateTime.UtcNow.AddHours(1)]))[0];
        Assert.Equal(NotificacoesService.PageSizeMaximo + 11, (await PendentesAsync(cliente)).NaoLidas);
        var antes = DateTime.UtcNow;

        var resposta = await LerTodasOkAsync(cliente);

        Assert.Equal((pendentes.Count, 1), (resposta.Marcadas, resposta.NaoLidas));
        Assert.InRange(resposta.LimiteUtc, antes.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(DateTimeKind.Utc, resposta.LimiteUtc.Kind);
        var gravadas = await NotificacoesDeAsync(usuarioId);
        foreach (var marcada in gravadas.Where(n => n.Id != novaDepoisDoLimite.Id))
        {
            Assert.Equal(resposta.LimiteUtc, marcada.LidaEmUtc);
            Assert.Equal((AvisoNotificacao.Lida, 0, resposta.LimiteUtc),
                (marcada.AvisoPendente!.Value, marcada.AvisoTentativas, marcada.AvisoProximaTentativaEmUtc!.Value));
        }

        var nova = gravadas.Single(n => n.Id == novaDepoisDoLimite.Id);
        Assert.Null(nova.LidaEmUtc);
        var depois = await PendentesAsync(cliente);
        Assert.Equal((1, novaDepoisDoLimite.Id), (depois.NaoLidas, depois.Items.Single().Id));
    }

    [Fact]
    public async Task Repetir_PreservaAPrimeiraDataDeLeitura_NaoReescreveNada_EAsLidasContinuamNoHistorico()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var semeadas = await SemearAsync(usuarioId, Passado(5));
        var lidaAntes = (await (await LerAsync(cliente, semeadas[0].Id)).Content.ReadFromJsonAsync<LeituraNotificacaoResponse>(Json))!;
        var individual = (await NotificacoesDeAsync(usuarioId)).Single(n => n.Id == semeadas[0].Id);
        await Task.Delay(30);

        var primeira = await LerTodasOkAsync(cliente);
        Assert.Equal((4, 0), (primeira.Marcadas, primeira.NaoLidas));
        var aposPrimeira = await NotificacoesDeAsync(usuarioId);
        Assert.Equal(Estado(individual), Estado(aposPrimeira.Single(n => n.Id == semeadas[0].Id)));
        Assert.Equal(lidaAntes.Notificacao.LidaEmUtc, aposPrimeira.Single(n => n.Id == semeadas[0].Id).LidaEmUtc);
        Assert.NotEqual(lidaAntes.Notificacao.LidaEmUtc, primeira.LimiteUtc);
        await Task.Delay(30);

        var repetida = await LerTodasOkAsync(cliente);

        Assert.Equal((0, 0), (repetida.Marcadas, repetida.NaoLidas));
        Assert.True(repetida.LimiteUtc > primeira.LimiteUtc);
        Assert.Equal(aposPrimeira.Select(Estado), (await NotificacoesDeAsync(usuarioId)).Select(Estado));

        var historico = await HistoricoCompletoAsync(cliente, "Lidas", pageSize: 2);
        Assert.Equal(semeadas.Select(s => s.Id).Order(), historico.Select(h => h.Id).Order());
        Assert.All(historico.Where(h => h.Id != semeadas[0].Id), h => Assert.Equal(primeira.LimiteUtc, h.LidaEmUtc));
        Assert.Equal(lidaAntes.Notificacao.LidaEmUtc, historico.Single(h => h.Id == semeadas[0].Id).LidaEmUtc);

        // A leitura individual continua compatível: abrir uma notificação já limpa devolve 200 com a mesma data.
        var reaberta = (await (await LerAsync(cliente, semeadas[1].Id)).Content.ReadFromJsonAsync<LeituraNotificacaoResponse>(Json))!;
        Assert.Equal((primeira.LimiteUtc, 0), (reaberta.Notificacao.LidaEmUtc!.Value, reaberta.NaoLidas));
    }

    [Fact]
    public async Task SemPendencias_200ComZero_ENadaEhGravado()
    {
        var (vazio, _) = await UsuarioAsync();
        var resposta = await LerTodasOkAsync(vazio);
        Assert.Equal((0, 0), (resposta.Marcadas, resposta.NaoLidas));

        var (soLidas, soLidasId) = await UsuarioAsync();
        var semeada = (await SemearAsync(soLidasId, Passado(1)))[0];
        Assert.Equal(HttpStatusCode.OK, (await LerAsync(soLidas, semeada.Id)).StatusCode);
        var antes = await NotificacoesDeAsync(soLidasId);

        var semNada = await LerTodasOkAsync(soLidas);
        Assert.Equal((0, 0), (semNada.Marcadas, semNada.NaoLidas));
        Assert.Equal(antes.Select(Estado), (await NotificacoesDeAsync(soLidasId)).Select(Estado));
    }

    [Fact]
    public async Task SoMarcaAsDoUsuarioDoToken_UsuarioNaQueryEhIgnorado_EOutroDestinatarioNaoMuda()
    {
        var (dono, donoId) = await UsuarioAsync();
        var (outro, outroId) = await UsuarioAsync("DIR");
        await SemearAsync(donoId, Passado(3));
        await SemearAsync(outroId, Passado(2));
        var doOutroAntes = await NotificacoesDeAsync(outroId);

        using var resposta = await LerTodasAsync(dono, $"?usuarioId={outroId}&destinatarioId={outroId}");
        var corpo = (await resposta.Content.ReadFromJsonAsync<LeituraTodasNotificacoesResponse>(Json))!;

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal((3, 0), (corpo.Marcadas, corpo.NaoLidas));
        Assert.All(await NotificacoesDeAsync(donoId), n => Assert.Equal(corpo.LimiteUtc, n.LidaEmUtc));
        Assert.Equal(doOutroAntes.Select(Estado), (await NotificacoesDeAsync(outroId)).Select(Estado));
        Assert.Equal(2, (await PendentesAsync(outro)).NaoLidas);
    }

    [Fact]
    public async Task ExigeAutenticacao_SemCache_EDesbravadorUsaOProprio()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await LerTodasAsync(factory.CreateClient())).StatusCode);

        var (desbravador, _) = await UsuarioAsync("DS");
        using var resposta = await LerTodasAsync(desbravador);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Swagger_DocumentaALeituraEmLote()
    {
        var documento = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json"))!;

        var post = documento["paths"]!["/api/Notificacoes/Leitura/Todas"]!["post"]!;
        Assert.Contains("todas", (string)post["summary"]!);
        Assert.Contains("limiteUtc", (string)post["description"]!);
        Assert.Null(post["parameters"]);
        Assert.Null(post["requestBody"]);
        foreach (var codigo in new[] { "200", "401" })
            Assert.NotNull(post["responses"]![codigo]);
        var resposta = documento["components"]!["schemas"]!["LeituraTodasNotificacoesResponse"]!["properties"]!.AsObject();
        Assert.Equal(["marcadas", "naoLidas", "limiteUtc"], resposta.Select(p => p.Key));
    }
}
