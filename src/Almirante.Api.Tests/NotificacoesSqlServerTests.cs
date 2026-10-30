using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Almirante.Api.Tests.NotificacoesTestKit;

namespace Almirante.Api.Tests;

[Trait("Category", "RequiresSqlServer")]
public sealed class NotificacoesSqlServerTests : IAsyncLifetime
{
    /// <summary>Publicador e monitor com intervalos curtos para os testes não dependerem dos 5 s/30 s de produção.</summary>
    public static Dictionary<string, string?> Rapido(int maxTentativas = 10) => new()
    {
        ["Notificacoes:VarreduraAvisosMs"] = "200",
        ["Notificacoes:AtrasoInicialAvisoMs"] = "100",
        ["Notificacoes:AtrasoMaximoAvisoMs"] = "400",
        ["Notificacoes:ReservaAvisoMs"] = "1000",
        ["Notificacoes:VerificacaoSessoesMs"] = "300",
        ["Notificacoes:MaxTentativasAviso"] = maxTentativas.ToString(),
    };

    private SqlServerApiFactory _factory = null!;
    private HttpClient _tesouraria = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqlServerApiFactory(Rapido());
        _tesouraria = await TestHelpers.CreateAuthenticatedClientAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _tesouraria.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<T> Db<T>(Func<Almirante.Api.Data.AlmiranteDbContext, Task<T>> acao) => TestHelpers.WithDbAsync(_factory, acao);

    private Task<List<Notificacao>> NotificacoesAsync() => Db(db => db.Notificacoes.AsNoTracking().ToListAsync());

    private Task<(HttpClient Client, Guid Id)> MembroAsync() => TestHelpers.CreateAuthenticatedClientForRoleAsync(_factory, "DS");

    private async Task AguardarAsync(Func<Task<bool>> condicao, string descricao, int segundos = 20, AlmiranteApiFactory? factory = null)
    {
        var fim = DateTime.UtcNow.AddSeconds(segundos);
        while (!await condicao())
        {
            if (DateTime.UtcNow > fim)
            {
                var erros = (factory ?? _factory).Logs.Entries.Where(e => e.Contains(nameof(Almirante.Api.Services.PublicadorAvisosNotificacoes))).Take(3);
                throw new TimeoutException(descricao + Environment.NewLine + string.Join(Environment.NewLine, erros));
            }

            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Evento_UmaNotificacaoPorParticipante_SemNotificacaoDeDebito_EOutroCadastroDoPasseioNaoEhAlcancado()
    {
        var membros = await EventosSqlSupport.MembrosAsync(_factory, 3);
        var chave = Guid.NewGuid().ToString();
        var primeiro = await EventosSqlSupport.CriarAsync(_tesouraria, new[] { membros[0], membros[1] }, chave: chave, local: "Sítio São José");
        var segundo = await EventosSqlSupport.CriarAsync(_tesouraria, membros[2], referencia: primeiro.Id, local: "Sítio São José", data: primeiro.DataEvento);
        await EventosSqlSupport.CriarAsync(_tesouraria, new[] { membros[0], membros[1] }, esperado: HttpStatusCode.OK, chave: chave, local: "Sítio São José");

        var notificacoes = await NotificacoesAsync();
        Assert.Equal(3, notificacoes.Count);
        Assert.All(notificacoes, n => Assert.Equal(TipoNotificacao.EventoCadastrado, n.Tipo));
        foreach (var (membro, evento) in new[] { (membros[0], primeiro), (membros[1], primeiro), (membros[2], segundo) })
        {
            var propria = Assert.Single(notificacoes, n => n.DestinatarioId == membro);
            Assert.Equal(evento.Id, propria.EventoId);
            Assert.Equal(evento.Lancamentos.Single(l => l.MembroId == membro).Id, propria.LancamentoId);
            Assert.Equal("Novo evento: Sítio São José", propria.Titulo);
            Assert.Contains("R$ 20,00", propria.Mensagem);
            Assert.Contains(evento.DataEvento.ToString("dd/MM/yyyy"), propria.Mensagem);
        }
    }

    [Fact]
    public async Task Evento_ReenviosConcorrentesComAMesmaChave_UmSoConjuntoDeNotificacoes()
    {
        var membros = await EventosSqlSupport.MembrosAsync(_factory, 3);
        var chave = Guid.NewGuid().ToString();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => EventosTestKit.PostAsync(_tesouraria, EventosTestKit.Corpo(membros), chave)));

        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(respostas, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        var notificacoes = await NotificacoesAsync();
        Assert.Equal(membros.Order(), notificacoes.Select(n => n.DestinatarioId).Order());
    }

    [Fact]
    public async Task DebitoIndividual_ReenviosConcorrentesComAMesmaChave_UmLancamentoEUmaNotificacao()
    {
        var (_, membroId) = await MembroAsync();
        var chave = Guid.NewGuid().ToString();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RegistrarDebitoAsync(_tesouraria, membroId, chave: chave)));

        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(respostas, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Equal(1, await Db(db => db.Lancamentos.CountAsync(l => l.MembroId == membroId)));
        Assert.Single(await NotificacoesAsync(), n => n.DestinatarioId == membroId);
    }

    [Fact]
    public async Task DebitoGeral_ReenviosConcorrentesComAMesmaChave_UmaNotificacaoPorLancamento()
    {
        await MembroAsync();
        await MembroAsync();
        var chave = Guid.NewGuid().ToString();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RegistrarGeralAsync(_tesouraria, chave)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var lancamentos = await Db(db => db.Lancamentos.AsNoTracking().Where(l => l.OperacaoId != null).ToListAsync());
        var notificacoes = await NotificacoesAsync();
        Assert.Equal(lancamentos.Select(l => l.Id).Order(), notificacoes.Select(n => n.LancamentoId!.Value).Order());
        Assert.Equal(notificacoes.Count, notificacoes.Select(n => n.DestinatarioId).Distinct().Count());
    }

    [Fact]
    public async Task Banco_RecusaNotificacaoDuplicadaParaOMesmoDestinatarioEOrigem()
    {
        var (_, membroId) = await MembroAsync();
        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(_tesouraria, membroId)).StatusCode);
        var original = Assert.Single(await NotificacoesAsync());

        var erro = await Assert.ThrowsAsync<SqlException>(() => EventosSqlSupport.EscalarAsync<int>(_factory, """
            INSERT INTO dbo.notificacoes (Id, DestinatarioId, Tipo, Titulo, Mensagem, CriadaEmUtc, LancamentoId, ChaveOrigem, AvisoTentativas)
            VALUES (NEWID(), @d, 1, 'x', 'y', SYSUTCDATETIME(), @l, @c, 0)
            """, ("@d", membroId), ("@l", original.LancamentoId!.Value), ("@c", original.ChaveOrigem)));

        Assert.Contains(erro.Number, new[] { 2601, 2627 });
        Assert.Contains("UX_notificacoes_destinatario_origem", erro.Message);
    }

    [Fact]
    public async Task LeiturasConcorrentes_TodasDevolvemAMesmaPrimeiraData()
    {
        var (membro, membroId) = await MembroAsync();
        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(_tesouraria, membroId)).StatusCode);
        var alvo = Assert.Single((await PendentesAsync(membro)).Items);

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => LerAsync(membro, alvo.Id)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var datas = (await Task.WhenAll(respostas.Select(r => r.Content.ReadFromJsonAsync<LeituraNotificacaoResponse>(Json))))
            .Select(l => l!.Notificacao.LidaEmUtc).Distinct().ToList();
        var data = Assert.Single(datas);
        Assert.Equal(data, (await Db(db => db.Notificacoes.AsNoTracking().SingleAsync(n => n.Id == alvo.Id))).LidaEmUtc);
    }

    [Fact]
    public async Task Pendentes_CursorComEmpateDeData_SegueAOrdemDoSqlServer_SemRepetirNemPular()
    {
        var (membro, membroId) = await MembroAsync();
        var mesmaHora = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await Db(async db =>
        {
            db.Notificacoes.AddRange(Enumerable.Range(0, 7).Select(i => new Notificacao
            {
                Id = Guid.NewGuid(), DestinatarioId = membroId, Tipo = TipoNotificacao.DebitoCadastrado, Titulo = "t", Mensagem = "m",
                CriadaEmUtc = i < 5 ? mesmaHora : mesmaHora.AddMinutes(i), ChaveOrigem = $"teste:{i}",
            }));
            return await db.SaveChangesAsync();
        });
        var esperada = await Db(db => db.Database.SqlQuery<Guid>(
            $"SELECT Id AS Value FROM dbo.notificacoes WHERE DestinatarioId = {membroId} AND LidaEmUtc IS NULL ORDER BY CriadaEmUtc DESC, Id DESC").ToListAsync());

        var percorrida = new List<Guid>();
        string? cursor = null;
        do
        {
            var pagina = await PendentesAsync(membro, pageSize: 2, cursor: cursor);
            percorrida.AddRange(pagina.Items.Select(i => i.Id));
            cursor = pagina.ProximoCursor;
        } while (cursor is not null);

        Assert.Equal(esperada, percorrida);
    }

    [Fact]
    public async Task Hub_AvisaSomenteODestinatario_EmTodasAsConexoes_EALeituraSincronizaAsOutras()
    {
        var (membro, membroId) = await MembroAsync();
        var (outro, _) = await MembroAsync();
        await using var abaLongPolling = Hub(_factory, Token(membro));
        await using var abaWebSocket = HubWebSocketComTokenNaQuery(_factory, Token(membro));
        await using var deOutro = Hub(_factory, Token(outro));
        var recebidosLp = new AvisosRecebidos(abaLongPolling);
        var recebidosWs = new AvisosRecebidos(abaWebSocket);
        var recebidosOutro = new AvisosRecebidos(deOutro);
        await Task.WhenAll(abaLongPolling.StartAsync(), abaWebSocket.StartAsync(), deOutro.StartAsync());

        var criado = await RegistrarDebitoAsync(_tesouraria, membroId);
        var lancamento = (await criado.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;

        var avisoLp = await recebidosLp.AguardarAsync("NotificacaoCriada");
        var avisoWs = await recebidosWs.AguardarAsync("NotificacaoCriada");
        var id = AvisosRecebidos.IdCriada(avisoLp);
        Assert.Equal(id, AvisosRecebidos.IdCriada(avisoWs));
        Assert.Equal(lancamento.Id, avisoLp.GetProperty("notificacao").GetProperty("lancamentoId").GetGuid());
        Assert.Equal("DebitoCadastrado", avisoLp.GetProperty("notificacao").GetProperty("tipo").GetString());
        Assert.Equal(1, avisoLp.GetProperty("naoLidas").GetInt32());
        Assert.EndsWith("Z", avisoLp.GetProperty("notificacao").GetProperty("criadaEmUtc").GetString());

        var leitura = await LerAsync(membro, id);
        Assert.Equal(HttpStatusCode.OK, leitura.StatusCode);
        var lidaEm = (await leitura.Content.ReadFromJsonAsync<LeituraNotificacaoResponse>(Json))!.Notificacao.LidaEmUtc;
        var lidaLp = await recebidosLp.AguardarAsync("NotificacaoLida");
        var lidaWs = await recebidosWs.AguardarAsync("NotificacaoLida");
        foreach (var lida in new[] { lidaLp, lidaWs })
        {
            Assert.Equal(id, lida.GetProperty("notificacaoId").GetGuid());
            Assert.Equal(0, lida.GetProperty("naoLidas").GetInt32());
            Assert.Equal(lidaEm, lida.GetProperty("lidaEmUtc").GetDateTime());
        }

        await Task.Delay(1000);
        Assert.Empty(recebidosOutro.Todos);
        Assert.Null(await Db(db => db.Notificacoes.Where(n => n.Id == id).Select(n => n.AvisoPendente).SingleAsync()));
    }

    [Fact]
    public async Task UsuarioDesconectado_EncontraANotificacaoNoProximoAcesso_EReconexaoRecuperaPendencias()
    {
        var (membro, membroId) = await MembroAsync();
        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(_tesouraria, membroId, descricao: "Antes de conectar")).StatusCode);
        await AguardarAsync(async () => await Db(db => db.Notificacoes.AllAsync(n => n.AvisoPendente == null)), "aviso emitido sem conexões");

        await using var conexao = Hub(_factory, Token(membro));
        var recebidos = new AvisosRecebidos(conexao);
        await conexao.StartAsync();
        Assert.Contains("Antes de conectar", Assert.Single((await PendentesAsync(membro)).Items).Mensagem);

        await conexao.StopAsync();
        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(_tesouraria, membroId, descricao: "Durante a queda")).StatusCode);
        await AguardarAsync(async () => await Db(db => db.Notificacoes.AllAsync(n => n.AvisoPendente == null)), "aviso emitido durante a queda");
        await conexao.StartAsync();

        var pendentes = await PendentesAsync(membro);
        Assert.Equal(2, pendentes.NaoLidas);
        Assert.Contains(pendentes.Items, n => n.Mensagem.Contains("Durante a queda"));
        Assert.Empty(recebidos.Todos);
    }

    [Fact]
    public async Task FalhaAoEmitir_NaoApagaANotificacao_RetentaComAtraso_ERetomaDepoisDoReinicio()
    {
        using var banco = new SqlServerApiFactory(Rapido(maxTentativas: 50));
        var falhas = new EmissorQueFalha();
        Guid membroId;
        using (var instanciaComFalha = banco.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmissorAvisosNotificacoes>(falhas))))
        {
            var tesouraria = await TestHelpers.CreateAuthenticatedClientAsync(instanciaComFalha);
            (var membro, membroId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(instanciaComFalha, "DS");
            Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(tesouraria, membroId)).StatusCode);

            await AguardarAsync(() => Task.FromResult(falhas.Chamadas >= 3), "três tentativas de emissão");
            var pendente = await TestHelpers.WithDbAsync(instanciaComFalha, db => db.Notificacoes.AsNoTracking().SingleAsync());
            Assert.Equal((AvisoNotificacao.Criada, (DateTime?)null), (pendente.AvisoPendente!.Value, pendente.LidaEmUtc));
            Assert.True(pendente.AvisoTentativas >= 3);
            Assert.Single((await PendentesAsync(membro)).Items);
        }

        var gravador = new EmissorGravador();
        using var reiniciada = banco.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmissorAvisosNotificacoes>(gravador)));
        _ = reiniciada.Server;

        var emitido = await gravador.Emitidos.Reader.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.Equal((membroId, NotificacoesHub.EventoNotificacaoCriada), (emitido.Destinatario, emitido.Evento));
        await AguardarAsync(async () => await TestHelpers.WithDbAsync(reiniciada, db => db.Notificacoes.AllAsync(n => n.AvisoPendente == null)),
            "aviso concluído depois do reinício");
    }

    [Fact]
    public async Task Evento_FalhaQueEsgotaAsRetentativas_NaoGravaNotificacao()
    {
        var falha = new FalhaDeCommitInterceptor();
        using var factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(falha), overrides: Rapido());
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var membros = await EventosSqlSupport.MembrosAsync(factory, 2);

        falha.Armar(MomentoDaFalha.AntesDoCommit, vezes: 10);
        var resposta = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(membros));
        falha.Desarmar();

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal((0, 0), await TestHelpers.WithDbAsync(factory, async db => (await db.Eventos.CountAsync(), await db.Notificacoes.CountAsync())));
    }

    [Fact]
    public async Task Publicador_AvisoQueEsgotaAsTentativas_LidoDepoisDaReserva_ECanceladoNoMeioDoEnvio()
    {
        var (_, membroId) = await MembroAsync();
        Assert.Equal(HttpStatusCode.Created, (await RegistrarDebitoAsync(_tesouraria, membroId)).StatusCode);
        await AguardarAsync(async () => await Db(db => db.Notificacoes.AllAsync(n => n.AvisoPendente == null)), "aviso inicial emitido");
        var id = Assert.Single(await NotificacoesAsync()).Id;
        // Próxima tentativa amanhã pelo relógio real: o publicador hospedado não alcança; o do teste roda com o relógio dois dias à frente.
        var amanha = DateTime.UtcNow.AddDays(1);
        Task Pendente(int tentativas, DateTime? lidaEm) => Db(db => db.Notificacoes.Where(n => n.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.AvisoPendente, AvisoNotificacao.Criada).SetProperty(n => n.AvisoTentativas, tentativas)
            .SetProperty(n => n.AvisoProximaTentativaEmUtc, amanha).SetProperty(n => n.LidaEmUtc, lidaEm)));
        var logs = new CapturingLoggerFactory();
        Almirante.Api.Services.PublicadorAvisosNotificacoes Publicador(IEmissorAvisosNotificacoes emissor) => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), new Almirante.Api.Services.SinalAvisosNotificacoes(), emissor,
            new RelogioManualDeTeste(DateTimeOffset.UtcNow.AddDays(2)),
            Microsoft.Extensions.Options.Options.Create(new Almirante.Api.Options.NotificacoesOptions { MaxTentativasAviso = 2 }),
            logs.CreateLogger<Almirante.Api.Services.PublicadorAvisosNotificacoes>());

        // A última tentativa falha: o aviso é abandonado, e a notificação continua na consulta de pendências.
        await Pendente(1, null);
        Assert.Equal(1, await Publicador(new EmissorQueFalha()).ProcessarLoteAsync(CancellationToken.None));
        Assert.Contains(logs.Entries, e => e.Contains("abandonado após 2 tentativas", StringComparison.Ordinal));
        Assert.Equal(0, await Publicador(new EmissorQueFalha()).ProcessarLoteAsync(CancellationToken.None));

        // Lida entre a reserva e o envio: não há o que emitir, e o aviso é concluído sem chamar o hub.
        await Pendente(0, DateTime.UtcNow);
        var gravador = new EmissorGravador();
        Assert.Equal(1, await Publicador(gravador).ProcessarLoteAsync(CancellationToken.None));
        Assert.False(gravador.Emitidos.Reader.TryRead(out _));
        Assert.Null(Assert.Single(await NotificacoesAsync()).AvisoPendente);

        // Encerramento no meio do envio: o cancelamento sobe, e o aviso continua pendente para o próximo início.
        await Pendente(0, null);
        using var parada = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Publicador(new EmissorQueCancela(parada)).ProcessarLoteAsync(parada.Token));
        Assert.Equal(AvisoNotificacao.Criada, Assert.Single(await NotificacoesAsync()).AvisoPendente);
    }

    private sealed class EmissorQueCancela(CancellationTokenSource parada) : IEmissorAvisosNotificacoes
    {
        public Task EmitirAsync(Guid destinatarioId, string evento, object payload, CancellationToken ct)
        {
            parada.Cancel();
            throw new OperationCanceledException(ct);
        }
    }

    [Fact]
    public async Task Evento_CommitComConfirmacaoPerdida_NaoDuplicaNotificacoes()
    {
        var falha = new FalhaDeCommitInterceptor();
        using var factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(falha), overrides: Rapido());
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var membros = await EventosSqlSupport.MembrosAsync(factory, 2);

        falha.Armar(MomentoDaFalha.DepoisDoCommit);
        var resposta = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(membros));
        falha.Desarmar();

        Assert.Contains(resposta.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        var notificacoes = await TestHelpers.WithDbAsync(factory, db => db.Notificacoes.AsNoTracking().ToListAsync());
        Assert.Equal(membros.Order(), notificacoes.Select(n => n.DestinatarioId).Order());
    }

    [Fact]
    public async Task DebitoIndividualSemChave_CommitComConfirmacaoPerdida_201SemDuplicarLancamentoNemNotificacao()
    {
        var falha = new FalhaDeCommitInterceptor();
        using var factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(falha), overrides: Rapido());
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Membro", "DS");

        falha.Armar(MomentoDaFalha.DepoisDoCommit);
        var resposta = await RegistrarDebitoAsync(client, membro.Id);
        falha.Desarmar();

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criado = (await resposta.Content.ReadFromJsonAsync<LancamentoDto>(Json))!;
        var (lancamentos, notificacoes) = await TestHelpers.WithDbAsync(factory, async db => (
            await db.Lancamentos.AsNoTracking().Where(l => l.MembroId == membro.Id).ToListAsync(),
            await db.Notificacoes.AsNoTracking().ToListAsync()));
        Assert.Equal(criado.Id, Assert.Single(lancamentos).Id);
        Assert.Equal(criado.Id, Assert.Single(notificacoes).LancamentoId);
    }

    [Fact]
    public async Task DebitoIndividual_FalhaQueEsgotaAsRetentativas_NaoGravaNotificacao()
    {
        var falha = new FalhaDeCommitInterceptor();
        using var factory = EventosSqlSupport.FabricaComRetry(o => o.AddInterceptors(falha), overrides: Rapido());
        var client = await TestHelpers.CreateAuthenticatedClientAsync(factory);
        var membro = await TestHelpers.AddUsuarioAsync(factory, "Membro", "DS");

        falha.Armar(MomentoDaFalha.AntesDoCommit, vezes: 10);
        var resposta = await RegistrarDebitoAsync(client, membro.Id);
        falha.Desarmar();

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal((0, 0), await TestHelpers.WithDbAsync(factory, async db =>
            (await db.Lancamentos.CountAsync(l => l.MembroId == membro.Id), await db.Notificacoes.CountAsync())));
    }

    [Fact]
    public async Task Hub_LogoutEncerraAConexaoAberta()
    {
        var (membro, _) = await MembroAsync();
        await using var conexao = Hub(_factory, Token(membro));
        var fechada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Closed += _ => { fechada.TrySetResult(); return Task.CompletedTask; };
        await conexao.StartAsync();

        var sessao = Sessao(Token(membro));
        await EventosSqlSupport.EscalarAsync<int>(_factory, "UPDATE dbo.AuthSessions SET RevokedAtUtc = SYSUTCDATETIME(), RevocationReason = 'logout' WHERE Id = @s",
            ("@s", sessao));

        await fechada.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class EmissorQueFalha : IEmissorAvisosNotificacoes
    {
        private int _chamadas;
        public int Chamadas => Volatile.Read(ref _chamadas);

        public Task EmitirAsync(Guid destinatarioId, string evento, object payload, CancellationToken ct)
        {
            Interlocked.Increment(ref _chamadas);
            throw new HubException("backplane indisponível (simulado)");
        }
    }

    private sealed class EmissorGravador : IEmissorAvisosNotificacoes
    {
        public System.Threading.Channels.Channel<(Guid Destinatario, string Evento, object Payload)> Emitidos { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(Guid, string, object)>();

        public Task EmitirAsync(Guid destinatarioId, string evento, object payload, CancellationToken ct) =>
            Emitidos.Writer.WriteAsync((destinatarioId, evento, payload), ct).AsTask();
    }
}
