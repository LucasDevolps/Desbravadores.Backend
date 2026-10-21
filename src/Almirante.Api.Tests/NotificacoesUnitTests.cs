using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Hubs;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Tests;

/// <summary>
/// Testes unitários das regras da issue #130: sem host HTTP e sem SQL Server. As garantias de transação, índice único e
/// concorrência ficam em NotificacoesSqlServerTests (integração com SQL Server real).
/// </summary>
public sealed class NotificacoesUnitTests
{
    private static readonly DateTime Agora = new(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc);

    private static Lancamento Lancamento(Guid? membro, TipoFluxoLancamento fluxo = TipoFluxoLancamento.Entrada, Guid? eventoId = null, decimal valor = 50m) => new()
    {
        Id = Guid.NewGuid(), MembroId = membro, Finalidade = eventoId is null ? "Mensalidade" : null, Descricao = "Outubro",
        Categoria = eventoId is null ? CategoriaLancamento.Clube : CategoriaLancamento.Evento, TipoFluxo = fluxo, Valor = valor,
        Vencimento = new DateOnly(2026, 11, 10), EventoId = eventoId,
    };

    private static Evento Evento() => new() { Id = Guid.NewGuid(), Local = "Parque Ibirapuera", DataEvento = new DateOnly(2026, 11, 15) };

    // ---- Destinatários ----

    [Fact]
    public void DebitoIndividual_UmaNotificacaoParaOMembroDoLancamento()
    {
        var membro = Guid.NewGuid();
        var lancamento = Lancamento(membro);

        var notificacao = Assert.Single(NotificacoesFabrica.ParaDebitos([lancamento], Agora));

        Assert.Equal((membro, TipoNotificacao.DebitoCadastrado, lancamento.Id, (Guid?)null, Agora),
            (notificacao.DestinatarioId, notificacao.Tipo, notificacao.LancamentoId!.Value, notificacao.EventoId, notificacao.CriadaEmUtc));
        Assert.Equal($"lancamento:{lancamento.Id:N}", notificacao.ChaveOrigem);
        Assert.Null(notificacao.LidaEmUtc);
    }

    [Fact]
    public void DebitoGeral_CadaMembroContempladoRecebeAPropria_ENinguemMais()
    {
        var contemplados = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var naoContemplado = Guid.NewGuid();
        var lancamentos = contemplados.Select(m => Lancamento(m)).ToList();

        var notificacoes = NotificacoesFabrica.ParaDebitos(lancamentos, Agora);

        Assert.Equal(contemplados.Order(), notificacoes.Select(n => n.DestinatarioId).Order());
        Assert.All(notificacoes, n => Assert.Equal(lancamentos.Single(l => l.Id == n.LancamentoId).MembroId, n.DestinatarioId));
        Assert.DoesNotContain(notificacoes, n => n.DestinatarioId == naoContemplado);
        Assert.Equal(notificacoes.Count, notificacoes.Select(n => (n.DestinatarioId, n.ChaveOrigem)).Distinct().Count());
    }

    [Fact]
    public void Saida_OuLancamentoSemMembro_NaoGeraNotificacao()
    {
        var lancamentos = new[] { Lancamento(Guid.NewGuid(), TipoFluxoLancamento.Saida), Lancamento(null) };

        Assert.Empty(NotificacoesFabrica.ParaDebitos(lancamentos, Agora));
    }

    [Fact]
    public void Evento_UmaNotificacaoPorParticipante_DoTipoEvento_SemNotificacaoDeDebito()
    {
        var evento = Evento();
        var participantes = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var naoSelecionado = Guid.NewGuid();
        var lancamentos = participantes.Select(m => Lancamento(m, eventoId: evento.Id, valor: 20m)).ToList();

        var notificacoes = NotificacoesFabrica.ParaEvento(evento, lancamentos, Agora);

        Assert.Equal(participantes.Order(), notificacoes.Select(n => n.DestinatarioId).Order());
        Assert.All(notificacoes, n =>
        {
            Assert.Equal((TipoNotificacao.EventoCadastrado, evento.Id, $"evento:{evento.Id:N}"), (n.Tipo, n.EventoId!.Value, n.ChaveOrigem));
            Assert.Equal(lancamentos.Single(l => l.MembroId == n.DestinatarioId).Id, n.LancamentoId);
            Assert.Contains("Parque Ibirapuera", n.Mensagem);
            Assert.Contains("15/11/2026", n.Mensagem);
            Assert.Contains("R$ 20,00", n.Mensagem);
        });
        Assert.DoesNotContain(notificacoes, n => n.Tipo == TipoNotificacao.DebitoCadastrado || n.DestinatarioId == naoSelecionado);
    }

    [Fact]
    public void Evento_LancamentoRepetidoDoMesmoParticipante_OuDeOutroCadastro_NaoDuplicaNemVaza()
    {
        var evento = Evento();
        var membro = Guid.NewGuid();
        var deOutroCadastro = Lancamento(Guid.NewGuid(), eventoId: Guid.NewGuid());

        var notificacoes = NotificacoesFabrica.ParaEvento(evento,
            [Lancamento(membro, eventoId: evento.Id), Lancamento(membro, eventoId: evento.Id), deOutroCadastro], Agora);

        Assert.Equal(membro, Assert.Single(notificacoes).DestinatarioId);
    }

    // ---- Deduplicação (idempotência) ----

    private static RegistrarLancamentoRequest Request(Guid? membro, bool geral = false, decimal valor = 50m) => new()
    {
        MembroId = geral ? null : membro, Finalidade = "Mensalidade", Descricao = " Outubro ", Categoria = CategoriaLancamento.Clube,
        TipoFluxo = TipoFluxoLancamento.Entrada, Valor = valor, Vencimento = new DateOnly(2026, 11, 10), AplicarATodosOsMembros = geral,
    };

    [Fact]
    public void HashIdempotente_MesmaSolicitacaoMesmoHash_MembroValorOuModoDiferentesMudamOHash()
    {
        var membro = Guid.NewGuid();
        var hash = LancamentoGeralService.CalcularHash(Request(membro));

        Assert.Equal(hash, LancamentoGeralService.CalcularHash(Request(membro)));
        Assert.NotEqual(hash, LancamentoGeralService.CalcularHash(Request(Guid.NewGuid())));
        Assert.NotEqual(hash, LancamentoGeralService.CalcularHash(Request(membro, valor: 51m)));
        Assert.NotEqual(hash, LancamentoGeralService.CalcularHash(Request(null, geral: true)));
    }

    [Fact]
    public void HashDoModoGeral_ContinuaIgualAoDasOperacoesJaGravadas()
    {
        var legado = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            "Mensalidade", "Outubro", CategoriaLancamento.Clube, "Entrada", 50m.ToString("F2", CultureInfo.InvariantCulture), "2026-11-10"))));

        Assert.Equal(legado, LancamentoGeralService.CalcularHash(Request(null, geral: true)));
    }

    // ---- Leitura e listagem (serviço com DbContext em memória e relógio controlado) ----

    private sealed class Cenario
    {
        private readonly string _banco = Guid.NewGuid().ToString();
        public MutableTimeProvider Relogio { get; } = new() { Now = new DateTimeOffset(Agora) };
        public Guid Membro { get; } = Guid.NewGuid();
        public Guid Outro { get; } = Guid.NewGuid();

        public AlmiranteDbContext Db() => new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseInMemoryDatabase(_banco).Options);

        public NotificacoesService Servico(AlmiranteDbContext db) => new(db, Relogio, new SinalAvisosNotificacoes());

        public async Task<List<Notificacao>> SemearAsync(Guid destinatario, params int[] minutosAtras)
        {
            await using var db = Db();
            var lista = minutosAtras.Select(m => new Notificacao
            {
                Id = Guid.NewGuid(), DestinatarioId = destinatario, Tipo = TipoNotificacao.DebitoCadastrado, Titulo = "t", Mensagem = "m",
                CriadaEmUtc = Agora.AddMinutes(-m), ChaveOrigem = $"teste:{Guid.NewGuid():N}",
            }).ToList();
            db.Notificacoes.AddRange(lista);
            await db.SaveChangesAsync();
            return lista;
        }

        public async Task<List<Notificacao>> TodasAsync()
        {
            await using var db = Db();
            return await db.Notificacoes.AsNoTracking().OrderBy(n => n.Id).ToListAsync();
        }
    }

    [Fact]
    public async Task Leitura_GravaAPrimeiraData_ERepetirPreservaAOriginal()
    {
        var c = new Cenario();
        var alvo = (await c.SemearAsync(c.Membro, 5, 3))[0];

        LeituraNotificacaoResponse? primeira, segunda;
        await using (var db = c.Db()) primeira = await c.Servico(db).MarcarComoLidaAsync(new(c.Membro, alvo.Id), CancellationToken.None);
        c.Relogio.Now = c.Relogio.Now.AddHours(2);
        await using (var db = c.Db()) segunda = await c.Servico(db).MarcarComoLidaAsync(new(c.Membro, alvo.Id), CancellationToken.None);

        Assert.Equal((Agora, 1), (primeira!.Notificacao.LidaEmUtc!.Value, primeira.NaoLidas));
        Assert.Equal((Agora, 1), (segunda!.Notificacao.LidaEmUtc!.Value, segunda.NaoLidas));
        var gravada = (await c.TodasAsync()).Single(n => n.Id == alvo.Id);
        Assert.Equal((Agora, AvisoNotificacao.Lida), (gravada.LidaEmUtc!.Value, gravada.AvisoPendente!.Value));
    }

    [Fact]
    public async Task Leitura_DeOutroDestinatario_DevolveNulo_ENaoAlteraNada()
    {
        var c = new Cenario();
        var alheia = (await c.SemearAsync(c.Outro, 1))[0];
        var antes = await c.TodasAsync();

        await using (var db = c.Db())
        {
            Assert.Null(await c.Servico(db).MarcarComoLidaAsync(new(c.Membro, alheia.Id), CancellationToken.None));
            Assert.Null(await c.Servico(db).MarcarComoLidaAsync(new(c.Membro, Guid.NewGuid()), CancellationToken.None));
        }

        Assert.Equal(antes, await c.TodasAsync(), new MesmoEstado());
    }

    [Fact]
    public async Task Leitura_DeUmDestinatario_NaoAlteraAsNotificacoesDosOutros()
    {
        var c = new Cenario();
        var minha = (await c.SemearAsync(c.Membro, 2))[0];
        var doOutro = (await c.SemearAsync(c.Outro, 2))[0];

        await using (var db = c.Db()) await c.Servico(db).MarcarComoLidaAsync(new(c.Membro, minha.Id), CancellationToken.None);

        var outro = (await c.TodasAsync()).Single(n => n.Id == doOutro.Id);
        Assert.Equal(((DateTime?)null, (AvisoNotificacao?)null), (outro.LidaEmUtc, outro.AvisoPendente));
    }

    [Fact]
    public async Task Listagem_NaoMarcaComoLida_NemMudaOAvisoPendente()
    {
        var c = new Cenario();
        await c.SemearAsync(c.Membro, 3, 2, 1);
        var antes = await c.TodasAsync();

        await using (var db = c.Db())
        {
            await c.Servico(db).ListarPendentesAsync(new(c.Membro, 20, null), CancellationToken.None);
            await c.Servico(db).ListarPendentesAsync(new(c.Membro, 1, null), CancellationToken.None);
        }

        Assert.Equal(antes, await c.TodasAsync(), new MesmoEstado());
    }

    [Fact]
    public async Task Listagem_SoDoDestinatario_MaisRecentePrimeiro_ComContadorTotal_EPaginasSemRepetir()
    {
        var c = new Cenario();
        var minhas = await c.SemearAsync(c.Membro, 30, 10, 20);
        await c.SemearAsync(c.Outro, 5);
        var lidaAntes = (await c.SemearAsync(c.Membro, 1))[0];
        await using (var db = c.Db()) await c.Servico(db).MarcarComoLidaAsync(new(c.Membro, lidaAntes.Id), CancellationToken.None);

        var vistas = new List<NotificacaoDto>();
        string? cursor = null;
        await using (var db = c.Db())
        {
            do
            {
                var pagina = await c.Servico(db).ListarPendentesAsync(new(c.Membro, 2, cursor), CancellationToken.None);
                Assert.Equal(3, pagina.NaoLidas);
                vistas.AddRange(pagina.Items);
                cursor = pagina.ProximoCursor;
            } while (cursor is not null);
        }

        Assert.Equal(minhas.OrderByDescending(n => n.CriadaEmUtc).Select(n => n.Id), vistas.Select(v => v.Id));
    }

    [Fact]
    public async Task Listagem_SemPendencias_ListaVazia_ESemCursor()
    {
        var c = new Cenario();
        await c.SemearAsync(c.Outro, 1);

        await using var db = c.Db();
        var pagina = await c.Servico(db).ListarPendentesAsync(new(c.Membro, 20, null), CancellationToken.None);

        Assert.Equal((0, 0, (string?)null), (pagina.Items.Count, pagina.NaoLidas, pagina.ProximoCursor));
    }

    private sealed class MesmoEstado : IEqualityComparer<Notificacao>
    {
        public bool Equals(Notificacao? a, Notificacao? b) => a is not null && b is not null &&
            (a.Id, a.DestinatarioId, a.LidaEmUtc, a.AvisoPendente, a.AvisoTentativas, a.AvisoProximaTentativaEmUtc) ==
            (b.Id, b.DestinatarioId, b.LidaEmUtc, b.AvisoPendente, b.AvisoTentativas, b.AvisoProximaTentativaEmUtc);

        public int GetHashCode(Notificacao n) => n.Id.GetHashCode();
    }

    // ---- Publicação e retentativas ----

    private static Notificacao Notificacao(DateTime? lidaEm = null) => new()
    {
        Id = Guid.NewGuid(), DestinatarioId = Guid.NewGuid(), Tipo = TipoNotificacao.DebitoCadastrado, Titulo = "t", Mensagem = "m",
        CriadaEmUtc = Agora, ChaveOrigem = "teste", LidaEmUtc = lidaEm,
    };

    [Fact]
    public void Aviso_CriadaNaoLida_EmiteNotificacaoCriadaComONovoTotal()
    {
        var notificacao = Notificacao();

        var (evento, payload) = PublicadorAvisosNotificacoes.MontarAviso(notificacao, AvisoNotificacao.Criada, 3)!.Value;

        Assert.Equal(NotificacoesHub.EventoNotificacaoCriada, evento);
        var aviso = Assert.IsType<NotificacaoCriadaAviso>(payload);
        Assert.Equal((notificacao.Id, 3), (aviso.Notificacao.Id, aviso.NaoLidas));
    }

    [Fact]
    public void Aviso_CriadaJaLida_NaoEmite_EAvisoDeLeituraLevaADataGravada()
    {
        var lida = Notificacao(Agora.AddMinutes(1));

        Assert.Null(PublicadorAvisosNotificacoes.MontarAviso(lida, AvisoNotificacao.Criada, 0));
        Assert.Null(PublicadorAvisosNotificacoes.MontarAviso(Notificacao(), AvisoNotificacao.Lida, 0));
        var (evento, payload) = PublicadorAvisosNotificacoes.MontarAviso(lida, AvisoNotificacao.Lida, 2)!.Value;
        Assert.Equal(NotificacoesHub.EventoNotificacaoLida, evento);
        Assert.Equal(new NotificacaoLidaAviso(lida.Id, lida.LidaEmUtc!.Value, 2), payload);
    }

    [Fact]
    public void Retentativa_AtrasoDobraACadaFalha_ComPisoETeto()
    {
        var opcoes = new NotificacoesOptions { AtrasoInicialAvisoMs = 5_000, AtrasoMaximoAvisoMs = 300_000 };

        var atrasos = Enumerable.Range(1, 10).Select(t => PublicadorAvisosNotificacoes.Atraso(t, opcoes).TotalSeconds).ToList();

        Assert.Equal([5, 10, 20, 40, 80, 160, 300, 300, 300, 300], atrasos);
        Assert.Equal(5, PublicadorAvisosNotificacoes.Atraso(0, opcoes).TotalSeconds);
        Assert.Equal(300, PublicadorAvisosNotificacoes.Atraso(int.MaxValue, opcoes).TotalSeconds);
        Assert.Equal(10, new NotificacoesOptions().MaxTentativasAviso);
    }

    [Fact]
    public async Task Sinal_VariosSinaisViramUmaSoVarredura_ESemSinalEsperaOPrazo()
    {
        var sinal = new SinalAvisosNotificacoes();
        sinal.Sinalizar();
        sinal.Sinalizar();
        sinal.Sinalizar();

        var acordou = System.Diagnostics.Stopwatch.StartNew();
        await sinal.AguardarAsync(TimeSpan.FromSeconds(10), TimeProvider.System, CancellationToken.None);
        Assert.True(acordou.Elapsed < TimeSpan.FromSeconds(2));

        var semSinal = System.Diagnostics.Stopwatch.StartNew();
        await sinal.AguardarAsync(TimeSpan.FromMilliseconds(300), TimeProvider.System, CancellationToken.None);
        Assert.True(semSinal.Elapsed >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void UsuarioDoHub_EhOUsuarioIdDoTokenNoFormatoD()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString("D"), UsuarioIdProvider.Formatar(id));
    }
}
