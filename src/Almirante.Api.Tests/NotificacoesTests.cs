using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Hubs;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Almirante.Api.Tests.NotificacoesTestKit;

namespace Almirante.Api.Tests;

public sealed class NotificacoesTests(AlmiranteApiFactory factory) : IClassFixture<AlmiranteApiFactory>
{
    private Task<(HttpClient Client, Guid UsuarioId)> UsuarioAsync(string role = "DS") =>
        TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, role);

    private Task<List<Notificacao>> NotificacoesDeAsync(Guid destinatarioId) =>
        TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AsNoTracking().Where(n => n.DestinatarioId == destinatarioId).ToListAsync());

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
    public async Task DebitoIndividual_NotificaSomenteOMembroDoLancamento_ComValorEVencimento()
    {
        var (tesouraria, _) = await UsuarioAsync("TES");
        var (membro, membroId) = await UsuarioAsync();
        var (outro, outroId) = await UsuarioAsync();

        var criado = await RegistrarDebitoAsync(tesouraria, membroId, valor: 1234.5m);

        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var lancamento = (await criado.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        var pendentes = await PendentesAsync(membro);
        var notificacao = Assert.Single(pendentes.Items);
        Assert.Equal((1, TipoNotificacao.DebitoCadastrado, lancamento.Id, (Guid?)null, (DateTime?)null),
            (pendentes.NaoLidas, notificacao.Tipo, notificacao.LancamentoId!.Value, notificacao.EventoId, notificacao.LidaEmUtc));
        Assert.Equal("Novo débito: Mensalidade", notificacao.Titulo);
        Assert.Contains("R$ 1.234,50", notificacao.Mensagem);
        Assert.Contains("Mensalidade (Outubro)", notificacao.Mensagem);
        Assert.Contains(DateOnly.ParseExact(Vencimento, "yyyy-MM-dd").ToString("dd/MM/yyyy"), notificacao.Mensagem);
        Assert.Equal(DateTimeKind.Utc, notificacao.CriadaEmUtc.Kind);

        var deOutro = await PendentesAsync(outro);
        Assert.Equal((0, 0, (string?)null), (deOutro.Items.Count, deOutro.NaoLidas, deOutro.ProximoCursor));
        Assert.Empty(await NotificacoesDeAsync(outroId));
    }

    [Fact]
    public async Task LancamentoDeSaida_ComMembro_NaoEhDebito_ENaoNotifica()
    {
        var (tesouraria, _) = await UsuarioAsync("TES");
        var (_, membroId) = await UsuarioAsync();

        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(tesouraria, membroId, tipoFluxo: "Saida")).StatusCode);

        Assert.Empty(await NotificacoesDeAsync(membroId));
    }

    [Fact]
    public async Task DebitoGeral_CadaMembroContempladoRecebeAPropria_LigadaAoProprioLancamento_SemDuplicarNoReenvio()
    {
        using var isolada = new AlmiranteApiFactory();
        var (tesouraria, tesourariaId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(isolada, "TES");
        var (membroA, membroAId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(isolada, "DS");
        var (membroB, membroBId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(isolada, "DS");
        var chave = Guid.NewGuid().ToString();

        var resposta = await RegistrarGeralAsync(tesouraria, chave);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var operacao = (await resposta.Content.ReadFromJsonAsync<LancamentoGeralResponse>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await RegistrarGeralAsync(tesouraria, chave)).StatusCode);

        var (lancamentos, notificacoes) = await TestHelpers.WithDbAsync(isolada, async db => (
            await db.Lancamentos.AsNoTracking().Where(l => l.OperacaoId == operacao.OperacaoId).ToListAsync(),
            await db.Notificacoes.AsNoTracking().ToListAsync()));
        Assert.Equal(operacao.LancamentosCriados, lancamentos.Count);
        Assert.Equal(lancamentos.Count, notificacoes.Count);
        Assert.All(notificacoes, n => Assert.Equal(lancamentos.Single(l => l.Id == n.LancamentoId).MembroId, n.DestinatarioId));
        Assert.Equal(lancamentos.Select(l => l.MembroId!.Value).Order(), notificacoes.Select(n => n.DestinatarioId).Order());
        Assert.Contains(notificacoes, n => n.DestinatarioId == tesourariaId);

        var pendentesA = await PendentesAsync(membroA);
        var pendentesB = await PendentesAsync(membroB);
        Assert.Equal(lancamentos.Single(l => l.MembroId == membroAId).Id, Assert.Single(pendentesA.Items).LancamentoId);
        Assert.Equal(lancamentos.Single(l => l.MembroId == membroBId).Id, Assert.Single(pendentesB.Items).LancamentoId);

        Assert.Equal(HttpStatusCode.OK, (await LerAsync(membroA, pendentesA.Items[0].Id)).StatusCode);
        Assert.Single((await PendentesAsync(membroB)).Items);
        Assert.Null((await TestHelpers.WithDbAsync(isolada, db => db.Notificacoes.AsNoTracking().SingleAsync(n => n.DestinatarioId == membroBId))).LidaEmUtc);
    }

    [Fact]
    public async Task DebitoGeralDeSaida_NaoNotificaNinguem()
    {
        using var isolada = new AlmiranteApiFactory();
        var (tesouraria, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(isolada, "TES");

        Assert.Equal(HttpStatusCode.OK, (await RegistrarGeralAsync(tesouraria, Guid.NewGuid().ToString(), tipoFluxo: "Saida")).StatusCode);

        Assert.Equal(0, await TestHelpers.WithDbAsync(isolada, db => db.Notificacoes.CountAsync()));
    }

    [Fact]
    public async Task DebitoIndividualComIdempotencyKey_ReenvioDevolveOMesmoLancamento_SemNovaNotificacao()
    {
        var (tesouraria, _) = await UsuarioAsync("TES");
        var (_, membroId) = await UsuarioAsync();
        var chave = Guid.NewGuid().ToString();

        var primeiro = await RegistrarDebitoAsync(tesouraria, membroId, chave: chave);
        var reenvio = await RegistrarDebitoAsync(tesouraria, membroId, chave: chave);

        Assert.Equal(HttpStatusCode.Created, primeiro.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reenvio.StatusCode);
        var original = (await primeiro.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        Assert.Equal(original.Id, (await reenvio.Content.ReadFromJsonAsync<LancamentoDto>(Json))!.Id);
        Assert.Equal(1, await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync(l => l.MembroId == membroId)));
        Assert.Single(await NotificacoesDeAsync(membroId));

        Assert.Equal(HttpStatusCode.Conflict, (await RegistrarDebitoAsync(tesouraria, membroId, valor: 51m, chave: chave)).StatusCode);
        var (_, outroMembroId) = await UsuarioAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await RegistrarDebitoAsync(tesouraria, outroMembroId, chave: chave)).StatusCode);
        Assert.Empty(await NotificacoesDeAsync(outroMembroId));
    }

    [Fact]
    public async Task DebitoIndividualSemIdempotencyKey_DuasSolicitacoesIguais_SaoDuasCobrancasLegitimas()
    {
        var (tesouraria, _) = await UsuarioAsync("TES");
        var (_, membroId) = await UsuarioAsync();

        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(tesouraria, membroId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(tesouraria, membroId)).StatusCode);

        Assert.Equal(2, await TestHelpers.WithDbAsync(factory, db => db.Lancamentos.CountAsync(l => l.MembroId == membroId)));
        var notificacoes = await NotificacoesDeAsync(membroId);
        Assert.Equal(2, notificacoes.Select(n => n.LancamentoId).Distinct().Count());
    }

    [Fact]
    public async Task ChaveDoModoGeral_ReusadaNoIndividual_Conflito()
    {
        using var isolada = new AlmiranteApiFactory();
        var (tesouraria, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(isolada, "TES");
        var membro = await TestHelpers.AddUsuarioAsync(isolada, "Membro", "DS");
        var chave = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await RegistrarGeralAsync(tesouraria, chave)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await RegistrarDebitoAsync(tesouraria, membro.Id, chave: chave)).StatusCode);
    }

    [Fact]
    public async Task Pendentes_OrdemDecrescente_PaginacaoPorCursor_NaoPulaItensDepoisDeUmaLeitura()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var baseUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var semeadas = await SemearAsync(usuarioId, baseUtc, baseUtc.AddMinutes(1), baseUtc.AddMinutes(2), baseUtc.AddMinutes(2), baseUtc.AddMinutes(3));

        var primeira = await PendentesAsync(cliente, pageSize: 2);
        Assert.Equal((5, 2), (primeira.NaoLidas, primeira.Items.Count));
        Assert.NotNull(primeira.ProximoCursor);
        Assert.Equal(baseUtc.AddMinutes(3), primeira.Items[0].CriadaEmUtc);

        Assert.Equal(HttpStatusCode.OK, (await LerAsync(cliente, primeira.Items[0].Id)).StatusCode);
        var segunda = await PendentesAsync(cliente, pageSize: 2, cursor: primeira.ProximoCursor);
        var terceira = await PendentesAsync(cliente, pageSize: 2, cursor: segunda.ProximoCursor);

        Assert.Equal(4, segunda.NaoLidas);
        Assert.Null(terceira.ProximoCursor);
        var percorridas = primeira.Items.Concat(segunda.Items).Concat(terceira.Items).ToList();
        Assert.Equal(semeadas.Select(s => s.Id).Order(), percorridas.Select(p => p.Id).Order());
        Assert.Equal(percorridas.Select(p => p.CriadaEmUtc).OrderDescending(), percorridas.Select(p => p.CriadaEmUtc));
        Assert.Equal(percorridas, percorridas.OrderByDescending(p => p.CriadaEmUtc).ThenByDescending(p => p.Id));
    }

    [Fact]
    public async Task Pendentes_SemNotificacoes_ListaVazia()
    {
        var (cliente, _) = await UsuarioAsync();

        var pendentes = await PendentesAsync(cliente);

        Assert.Equal((0, 0, 20, (string?)null), (pendentes.Items.Count, pendentes.NaoLidas, pendentes.PageSize, pendentes.ProximoCursor));
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=51")]
    [InlineData("pageSize=abc")]
    [InlineData("cursor=nao-e-um-cursor")]
    [InlineData("cursor=MTIzLmFiYw")]
    public async Task Pendentes_ParametrosInvalidos_400(string query)
    {
        var (cliente, _) = await UsuarioAsync();

        var resposta = await cliente.GetAsync($"/api/Notificacoes/Pendentes?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, corpo.GetProperty("status").GetInt32());
        Assert.True(corpo.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Listar_NaoMarcaComoLida_ENaoAlteraNada()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        await SemearAsync(usuarioId, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1));

        await PendentesAsync(cliente);
        var depois = await PendentesAsync(cliente);

        Assert.Equal(2, depois.NaoLidas);
        Assert.All(await NotificacoesDeAsync(usuarioId), n => Assert.Null(n.LidaEmUtc));
        var resposta = await cliente.GetAsync("/api/Notificacoes/Pendentes");
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Leitura_PersisteAPrimeiraData_EhIdempotente_ERemoveDasPendencias()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var alvo = (await SemearAsync(usuarioId, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1)))[0];

        var primeira = await LerAsync(cliente, alvo.Id);
        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);
        var leitura = (await primeira.Content.ReadFromJsonAsync<LeituraNotificacaoResponse>(Json))!;
        await Task.Delay(30);
        var repetida = (await (await LerAsync(cliente, alvo.Id)).Content.ReadFromJsonAsync<LeituraNotificacaoResponse>(Json))!;

        Assert.Equal((alvo.Id, 1), (leitura.Notificacao.Id, leitura.NaoLidas));
        Assert.NotNull(leitura.Notificacao.LidaEmUtc);
        Assert.Equal(leitura.Notificacao.LidaEmUtc, repetida.Notificacao.LidaEmUtc);
        Assert.Equal(1, repetida.NaoLidas);
        var salva = (await NotificacoesDeAsync(usuarioId)).Single(n => n.Id == alvo.Id);
        Assert.Equal(leitura.Notificacao.LidaEmUtc, salva.LidaEmUtc);
        Assert.Equal(AvisoNotificacao.Lida, salva.AvisoPendente);
        Assert.DoesNotContain((await PendentesAsync(cliente)).Items, n => n.Id == alvo.Id);
    }

    [Fact]
    public async Task Leitura_DeNotificacaoDeOutroUsuario_404IgualAoDeInexistente_ENaoAltera()
    {
        var (_, donoId) = await UsuarioAsync();
        var (intruso, _) = await UsuarioAsync();
        var alheia = (await SemearAsync(donoId, DateTime.UtcNow))[0];

        var deOutro = await LerAsync(intruso, alheia.Id);
        var inexistente = await LerAsync(intruso, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, deOutro.StatusCode);
        Assert.Equal(await TestHelpers.NormalizedProblemAsync(inexistente), await TestHelpers.NormalizedProblemAsync(deOutro));
        Assert.Null((await NotificacoesDeAsync(donoId)).Single().LidaEmUtc);
        Assert.DoesNotContain((await PendentesAsync(intruso)).Items, n => n.Id == alheia.Id);
    }

    [Fact]
    public async Task Endpoints_ExigemAutenticacao_EDesbravadorUsaOProprioModulo_SemGanharAcessoAdministrativo()
    {
        var anonimo = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/Notificacoes/Pendentes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LerAsync(anonimo, Guid.NewGuid())).StatusCode);

        var (desbravador, _) = await UsuarioAsync("DS");
        Assert.Equal(HttpStatusCode.OK, (await desbravador.GetAsync("/api/Notificacoes/Pendentes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desbravador.GetAsync("/api/Eventos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await desbravador.GetAsync("/api/Lancamentos")).StatusCode);

        var (diretoria, _) = await UsuarioAsync("DIR");
        Assert.Equal(HttpStatusCode.OK, (await diretoria.GetAsync("/api/Notificacoes/Pendentes")).StatusCode);
    }

    [Fact]
    public async Task Hub_SemToken_401_ETokenNaQueryNaoAutenticaForaDoHandshakeDoHub()
    {
        var (cliente, _) = await UsuarioAsync();
        var token = Token(cliente);
        var anonimo = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync("/hubs/notificacoes/negotiate?negotiateVersion=1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonimo.PostAsync($"/hubs/notificacoes/negotiate?negotiateVersion=1&access_token={token}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"/api/Notificacoes/Pendentes?access_token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"/api/Usuarios?access_token={token}")).StatusCode);

        await using var conexao = Hub(factory, "token-invalido");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => conexao.StartAsync());
    }

    [Fact]
    public async Task Hub_ConectaPorWebSocketComTokenNaQuery_SemGravarOTokenNosLogs()
    {
        var (cliente, usuarioId) = await UsuarioAsync();
        var token = Token(cliente);

        await using var conexao = HubWebSocketComTokenNaQuery(factory, token);
        await conexao.StartAsync();

        Assert.Equal(HubConnectionState.Connected, conexao.State);
        Assert.Contains(factory.Services.GetRequiredService<ConexoesNotificacoes>().Abertas(), c => c.UsuarioId == usuarioId);
        await conexao.StopAsync();
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hub_ClienteNaoTemMetodosParaEscolherDestinatarioOuEntrarEmGrupos()
    {
        var (cliente, _) = await UsuarioAsync();
        await using var conexao = Hub(factory, Token(cliente));
        await conexao.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => conexao.InvokeAsync("AddToGroup", "todos"));
        await Assert.ThrowsAsync<HubException>(() => conexao.InvokeAsync("SendToUser", Guid.NewGuid().ToString(), "oi"));
    }

    [Fact]
    public async Task MonitorDeSessoes_EncerraAConexaoQuandoASessaoEhRevogada()
    {
        var (cliente, _) = await UsuarioAsync();
        var token = Token(cliente);
        var (outro, _) = await UsuarioAsync();
        await using var revogada = Hub(factory, token);
        await using var vigente = Hub(factory, Token(outro));
        var fechada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        revogada.Closed += _ => { fechada.TrySetResult(); return Task.CompletedTask; };
        await revogada.StartAsync();
        await vigente.StartAsync();

        var sessao = Sessao(token);
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            var s = await db.AuthSessions.SingleAsync(x => x.Id == sessao);
            s.RevokedAtUtc = DateTime.UtcNow;
            s.RevocationReason = "logout";
            return await db.SaveChangesAsync();
        });
        var encerradas = await factory.Services.GetRequiredService<MonitorSessoesNotificacoes>().VerificarAsync(CancellationToken.None);

        Assert.True(encerradas >= 1);
        await fechada.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Connected, vigente.State);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/Notificacoes/Pendentes")).StatusCode);
    }

    [Fact]
    public async Task Hub_FechaAConexaoQuandoOTokenExpira()
    {
        var (cliente, _) = await UsuarioAsync();
        var curto = TokenComExpiracao(factory, Token(cliente), TimeSpan.FromSeconds(3));
        await using var conexao = Hub(factory, curto);
        var fechada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Closed += _ => { fechada.TrySetResult(); return Task.CompletedTask; };

        await conexao.StartAsync();

        await fechada.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HubConnectionState.Disconnected, conexao.State);
    }

    [Fact]
    public void Fabrica_TextosEmVarcharDaPagina1252_ComLimites()
    {
        Assert.Equal("R$ 1.234,50", NotificacoesFabrica.Moeda(1234.5m));
        Assert.Equal("R$ 0,00", NotificacoesFabrica.Moeda(0m));
        Assert.Equal("Ação ? dia", TextoVarchar.ParaPagina1252("Ação 🎉 dia"));
        Assert.Equal("a b", TextoVarchar.ParaPagina1252("a\nb"));

        var lancamento = new Lancamento
        {
            Id = Guid.NewGuid(), MembroId = Guid.NewGuid(), Finalidade = "Outros", Descricao = new string('x', 1200),
            Categoria = CategoriaLancamento.Clube, TipoFluxo = TipoFluxoLancamento.Entrada, Valor = 10m, Vencimento = new DateOnly(2026, 11, 3),
        };
        var notificacao = NotificacoesFabrica.DebitoCadastrado(lancamento, DateTime.UtcNow);
        Assert.Equal(NotificacoesFabrica.MensagemMaximo, notificacao.Mensagem.Length);
        Assert.EndsWith("…", notificacao.Mensagem);
        Assert.True(TextoVarchar.CabeNaPagina1252(notificacao.Mensagem));
        Assert.Equal((AvisoNotificacao.Criada, notificacao.CriadaEmUtc), (notificacao.AvisoPendente!.Value, notificacao.AvisoProximaTentativaEmUtc!.Value));
        Assert.Equal($"lancamento:{lancamento.Id:N}", notificacao.ChaveOrigem);

        Assert.False(NotificacoesFabrica.EhDebito(new Lancamento { Categoria = CategoriaLancamento.Clube, MembroId = Guid.NewGuid(), TipoFluxo = TipoFluxoLancamento.Saida }));
        Assert.False(NotificacoesFabrica.EhDebito(new Lancamento { Categoria = CategoriaLancamento.Clube, MembroId = null, TipoFluxo = TipoFluxoLancamento.Entrada }));

        var evento = new Evento { Id = Guid.NewGuid(), Local = new string('L', 200), DataEvento = new DateOnly(2026, 12, 5) };
        lancamento.Valor = 0m;
        var gratis = NotificacoesFabrica.EventoCadastrado(evento, lancamento, DateTime.UtcNow);
        Assert.Equal(NotificacoesFabrica.TituloMaximo, gratis.Titulo.Length);
        Assert.EndsWith("Não há valor a pagar por este evento.", gratis.Mensagem);
        Assert.Equal((TipoNotificacao.EventoCadastrado, evento.Id, lancamento.Id), (gratis.Tipo, gratis.EventoId!.Value, gratis.LancamentoId!.Value));
    }

    [Fact]
    public void Cursor_IdaEVolta_ERecusaConteudoInvalido()
    {
        var criada = new DateTime(2026, 10, 20, 13, 14, 15, DateTimeKind.Utc).AddTicks(1234567);
        var id = Guid.NewGuid();

        Assert.True(CursorNotificacoes.TentarLer(CursorNotificacoes.Criar(criada, id), out var lida, out var lidoId));
        Assert.Equal((criada, id, DateTimeKind.Utc), (lida, lidoId, lida.Kind));
        Assert.False(CursorNotificacoes.TentarLer("%%%", out _, out _));
        Assert.False(CursorNotificacoes.TentarLer(Convert.ToBase64String("1.2"u8.ToArray()).TrimEnd('='), out _, out _));
        Assert.False(CursorNotificacoes.TentarLer(new string('A', 200), out _, out _));
    }
}
