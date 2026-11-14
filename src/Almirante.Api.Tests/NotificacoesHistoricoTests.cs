using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;
using static Almirante.Api.Tests.NotificacoesTestKit;

namespace Almirante.Api.Tests;

public sealed class NotificacoesHistoricoTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private static readonly string[] Filtros = ["Todas", "Lidas", "NaoLidas"];

    private Task<(HttpClient Client, Guid UsuarioId)> UsuarioAsync(string role = "DS") =>
        TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);

    private Task<List<Notificacao>> NotificacoesDeAsync(Guid destinatarioId) =>
        TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AsNoTracking()
            .Where(n => n.DestinatarioId == destinatarioId).OrderBy(n => n.Id).ToListAsync());

    private async Task<List<Notificacao>> SemearAsync(Guid destinatarioId, params DateTime[] criadas)
    {
        var lista = criadas.Select((c, i) => new Notificacao
        {
            Id = Guid.NewGuid(), DestinatarioId = destinatarioId, Tipo = TipoNotificacao.DebitoCadastrado,
            Titulo = $"Teste {i}", Mensagem = $"Mensagem {i}", CriadaEmUtc = c, ChaveOrigem = $"teste:{Guid.NewGuid():N}",
        }).ToList();
        await TestHelpers.WithDbAsync(factory, async db => { db.Notificacoes.AddRange(lista); return await db.SaveChangesAsync(); });
        return lista;
    }

    [Fact]
    public async Task MesmoAviso_ApareceAntesEDepoisDaLeitura_ComLidaEmUtc_ELidasOIncluiEPendentesNao()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var agora = DateTime.UtcNow;
        var semeadas = await SemearAsync(usuarioId, agora.AddMinutes(-2), agora.AddMinutes(-1));
        var (alvo, outra) = (semeadas[0], semeadas[1]);

        var antes = await HistoricoAsync(cliente);
        Assert.Equal((FiltroLeituraNotificacoes.Todas, 2), (antes.Filtro, antes.NaoLidas));
        Assert.Null(antes.Items.Single(i => i.Id == alvo.Id).LidaEmUtc);
        Assert.Empty((await HistoricoAsync(cliente, "Lidas")).Items);

        var leitura = (await (await LerAsync(cliente, alvo.Id)).Content.ReadFromJsonAsync<LeituraNotificacaoResponse>(Json))!;
        var lidaEm = leitura.Notificacao.LidaEmUtc;
        Assert.NotNull(lidaEm);

        var depois = await HistoricoAsync(cliente);
        Assert.Equal([outra.Id, alvo.Id], depois.Items.Select(i => i.Id));
        Assert.Equal(lidaEm, depois.Items.Single(i => i.Id == alvo.Id).LidaEmUtc);
        Assert.Equal(leitura.Notificacao, depois.Items.Single(i => i.Id == alvo.Id));
        Assert.Equal(1, depois.NaoLidas);

        var lidas = await HistoricoAsync(cliente, "Lidas");
        Assert.Equal((alvo.Id, lidaEm), (lidas.Items.Single().Id, lidas.Items.Single().LidaEmUtc));
        var naoLidas = await HistoricoAsync(cliente, "NaoLidas");
        Assert.Equal(outra.Id, naoLidas.Items.Single().Id);
        Assert.Null(naoLidas.Items.Single().LidaEmUtc);
        Assert.DoesNotContain((await PendentesAsync(cliente)).Items, i => i.Id == alvo.Id);
    }

    [Fact]
    public async Task Paginacao_EstavelPorDataEId_ComDatasIguais_ENaoLidasDaCaixaInteiraEmTodaPaginaEFiltro()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var mesmaHora = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var semeadas = await SemearAsync(usuarioId,
            mesmaHora, mesmaHora, mesmaHora, mesmaHora, mesmaHora.AddMinutes(1), mesmaHora.AddMinutes(-1), mesmaHora.AddMinutes(1));
        var lidas = semeadas.Where((_, i) => i % 2 == 0).Select(s => s.Id).ToHashSet();
        foreach (var id in lidas)
        {
            Assert.Equal(HttpStatusCode.OK, (await LerAsync(cliente, id)).StatusCode);
        }

        var esperadas = semeadas.OrderByDescending(s => s.CriadaEmUtc).ThenByDescending(s => s.Id).Select(s => s.Id).ToList();
        foreach (var (filtro, esperada) in new[]
        {
            ("Todas", esperadas),
            ("Lidas", esperadas.Where(lidas.Contains).ToList()),
            ("NaoLidas", esperadas.Where(id => !lidas.Contains(id)).ToList()),
        })
        {
            var percorrida = new List<Guid>();
            string? cursor = null;
            do
            {
                var pagina = await HistoricoAsync(cliente, filtro, pageSize: 2, cursor: cursor);
                Assert.Equal(3, pagina.NaoLidas);
                Assert.Equal(2, pagina.PageSize);
                Assert.True(pagina.Items.Count <= 2);
                percorrida.AddRange(pagina.Items.Select(i => i.Id));
                cursor = pagina.ProximoCursor;
            } while (cursor is not null);

            Assert.Equal(esperada, percorrida);
        }

        var primeira = await HistoricoAsync(cliente, pageSize: 3);
        var novamente = await HistoricoAsync(cliente, pageSize: 3);
        Assert.Equal(primeira.Items.Select(i => i.Id), novamente.Items.Select(i => i.Id));
        Assert.Equal(primeira.ProximoCursor, novamente.ProximoCursor);
    }

    [Fact]
    public async Task LerEntreUmaPaginaEOutra_NaoFazOHistoricoPularNemRepetirItens()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var baseUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var semeadas = await SemearAsync(usuarioId, baseUtc, baseUtc.AddMinutes(1), baseUtc.AddMinutes(2), baseUtc.AddMinutes(3));

        var primeira = await HistoricoAsync(cliente, pageSize: 2);
        Assert.Equal(HttpStatusCode.OK, (await LerAsync(cliente, primeira.Items[0].Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LerAsync(cliente, semeadas[0].Id)).StatusCode);
        var segunda = await HistoricoAsync(cliente, pageSize: 2, cursor: primeira.ProximoCursor);

        Assert.Null(segunda.ProximoCursor);
        Assert.Equal(2, segunda.NaoLidas);
        Assert.Equal(semeadas.Select(s => s.Id).Reverse(), primeira.Items.Concat(segunda.Items).Select(i => i.Id));
        Assert.NotNull(segunda.Items.Single(i => i.Id == semeadas[0].Id).LidaEmUtc);
    }

    [Fact]
    public async Task HistoricoVazio_EmTodosOsFiltros()
    {
        var (cliente, _) = await UsuarioAsync();

        foreach (var filtro in Filtros)
        {
            var pagina = await HistoricoAsync(cliente, filtro);
            Assert.Equal((0, 0, 20, (string?)null), (pagina.Items.Count, pagina.NaoLidas, pagina.PageSize, pagina.ProximoCursor));
            Assert.Equal(Enum.Parse<FiltroLeituraNotificacoes>(filtro), pagina.Filtro);
        }

        var semFiltro = await cliente.GetFromJsonAsync<JsonElement>("/api/Notificacoes");
        Assert.Equal("Todas", semFiltro.GetProperty("filtro").GetString());
        Assert.Equal(0, semFiltro.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CadaFiltro_RetornaSoAsNotificacoesDoTitular_EOCursorDeOutroUsuarioNaoVaza()
    {
        var (dono, donoId) = await UsuarioAsync();
        var (intruso, intrusoId) = await UsuarioAsync("DIR");
        var agora = DateTime.UtcNow;
        var doDono = await SemearAsync(donoId, agora.AddMinutes(-3), agora.AddMinutes(-2), agora.AddMinutes(-1));
        var doIntruso = await SemearAsync(intrusoId, agora.AddMinutes(-3));
        Assert.Equal(HttpStatusCode.OK, (await LerAsync(dono, doDono[0].Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LerAsync(intruso, doDono[1].Id)).StatusCode);

        foreach (var filtro in Filtros)
        {
            Assert.All(await HistoricoCompletoAsync(dono, filtro), i => Assert.Contains(i.Id, doDono.Select(d => d.Id)));
            Assert.All(await HistoricoCompletoAsync(intruso, filtro), i => Assert.Equal(doIntruso[0].Id, i.Id));
        }

        Assert.Equal(3, (await HistoricoCompletoAsync(dono)).Count);
        Assert.Equal(2, (await HistoricoAsync(dono)).NaoLidas);
        Assert.Equal(1, (await HistoricoAsync(intruso)).NaoLidas);
        Assert.Empty((await HistoricoAsync(intruso, "Lidas")).Items);

        var cursorDoDono = (await HistoricoAsync(dono, pageSize: 1)).ProximoCursor;
        var comCursorAlheio = await HistoricoAsync(intruso, cursor: cursorDoDono);
        Assert.All(comCursorAlheio.Items, i => Assert.Equal(doIntruso[0].Id, i.Id));

        var comUsuarioNaQuery = await intruso.GetFromJsonAsync<HistoricoNotificacoesResponse>(
            $"/api/Notificacoes?usuarioId={donoId}&destinatarioId={donoId}", Json);
        Assert.Equal(doIntruso[0].Id, comUsuarioNaQuery!.Items.Single().Id);
    }

    [Fact]
    public async Task NenhumaConsulta_AlteraOEstadoDeLeitura()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var semeadas = await SemearAsync(usuarioId, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.OK, (await LerAsync(cliente, semeadas[0].Id)).StatusCode);
        var antes = await NotificacoesDeAsync(usuarioId);

        foreach (var filtro in Filtros)
        {
            await HistoricoAsync(cliente, filtro);
            await HistoricoCompletoAsync(cliente, filtro, pageSize: 1);
        }

        var depois = await NotificacoesDeAsync(usuarioId);
        Assert.Equal(
            antes.Select(n => (n.Id, n.LidaEmUtc, n.AvisoPendente, n.AvisoTentativas, n.AvisoProximaTentativaEmUtc)),
            depois.Select(n => (n.Id, n.LidaEmUtc, n.AvisoPendente, n.AvisoTentativas, n.AvisoProximaTentativaEmUtc)));
        var resposta = await cliente.GetAsync("/api/Notificacoes");
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("filtro=abc")]
    [InlineData("filtro=7")]
    [InlineData("filtro=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=51")]
    [InlineData("pageSize=abc")]
    [InlineData("cursor=nao-e-um-cursor")]
    [InlineData("cursor=MTIzLmFiYw")]
    public async Task ParametrosInvalidos_400(string query)
    {
        var (cliente, _) = await UsuarioAsync();

        var resposta = await cliente.GetAsync($"/api/Notificacoes?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, corpo.GetProperty("status").GetInt32());
        Assert.True(corpo.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData("lidas", FiltroLeituraNotificacoes.Lidas)]
    [InlineData("NAOLIDAS", FiltroLeituraNotificacoes.NaoLidas)]
    public async Task Filtro_AceitaONomeSemDiferenciarMaiusculas(string filtro, FiltroLeituraNotificacoes esperado)
    {
        var (cliente, _) = await UsuarioAsync();

        Assert.Equal(esperado, (await HistoricoAsync(cliente, filtro)).Filtro);
    }

    [Fact]
    public async Task ExigeAutenticacao_EDesbravadorUsaOProprioHistorico()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/Notificacoes")).StatusCode);

        var (desbravador, _) = await UsuarioAsync("DS");
        Assert.Equal(HttpStatusCode.OK, (await desbravador.GetAsync("/api/Notificacoes?filtro=Lidas")).StatusCode);
    }

    [Fact]
    public async Task Swagger_DocumentaOHistoricoComFiltroPaginacaoERespostas()
    {
        var documento = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json"))!;

        var get = documento["paths"]!["/api/Notificacoes"]!["get"]!;
        Assert.Contains("histórico", (string)get["summary"]!);
        Assert.Contains("naoLidas", (string)get["description"]!);
        var parametros = get["parameters"]!.AsArray();
        Assert.Equal(["filtro", "pageSize", "cursor"], parametros.Select(p => (string)p!["name"]!));
        Assert.DoesNotContain(parametros, p => ((string)p!["name"]!).Contains("usuario", StringComparison.OrdinalIgnoreCase));

        var filtro = parametros.Single(p => (string)p!["name"]! == "filtro")!["schema"]!;
        var referencia = (string?)filtro["$ref"] ?? (string?)filtro["allOf"]?[0]?["$ref"];
        var esquemaFiltro = referencia is null ? filtro : documento["components"]!["schemas"]![referencia.Split('/')[^1]]!;
        Assert.Equal(Filtros, esquemaFiltro["enum"]!.AsArray().Select(v => (string)v!));

        foreach (var codigo in new[] { "200", "400", "401" })
            Assert.NotNull(get["responses"]![codigo]);
        var resposta = documento["components"]!["schemas"]!["HistoricoNotificacoesResponse"]!["properties"]!.AsObject();
        Assert.Equal(["items", "filtro", "naoLidas", "pageSize", "proximoCursor"], resposta.Select(p => p.Key));
    }
}
