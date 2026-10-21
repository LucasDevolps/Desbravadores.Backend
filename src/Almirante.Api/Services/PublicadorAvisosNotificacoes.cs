using System.Data;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Hubs;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

/// <summary>
/// Emite pelo hub os avisos pendentes gravados junto com as notificações (outbox na própria linha de notificacoes).
/// Só lê o que já foi confirmado no banco, então nada é anunciado antes do commit; depois de um reinício retoma as pendências.
/// Entrega "pelo menos uma vez": o cliente deduplica pelo id da notificação. Falha no envio nunca desfaz a notificação.
/// </summary>
public sealed class PublicadorAvisosNotificacoes(IServiceScopeFactory scopes, SinalAvisosNotificacoes sinal, IEmissorAvisosNotificacoes emissor,
    TimeProvider clock, IOptions<NotificacoesOptions> options, ILogger<PublicadorAvisosNotificacoes> logger) : BackgroundService
{
    private const string ReservarSql = """
        WITH lote AS (
            SELECT TOP (@lote) Id, AvisoPendente, AvisoTentativas, AvisoProximaTentativaEmUtc
            FROM dbo.notificacoes WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE AvisoPendente IS NOT NULL AND AvisoProximaTentativaEmUtc <= @agora AND AvisoTentativas < @maximo
            ORDER BY AvisoProximaTentativaEmUtc, Id
        )
        UPDATE lote SET AvisoTentativas = AvisoTentativas + 1, AvisoProximaTentativaEmUtc = @reservadoAte
        OUTPUT inserted.Id, inserted.AvisoPendente, inserted.AvisoTentativas;
        """;

    private NotificacoesOptions Opcoes => options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var falhasSeguidas = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var loteCheio = false;
            try
            {
                loteCheio = await ProcessarLoteAsync(stoppingToken) >= Opcoes.LoteAvisos;
                falhasSeguidas = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                falhasSeguidas++;
                logger.LogError(exception, "Falha ao processar avisos de notificações ({Falhas} seguidas).", falhasSeguidas);
            }

            if (loteCheio)
            {
                continue;
            }

            var espera = falhasSeguidas == 0 ? TimeSpan.FromMilliseconds(Opcoes.VarreduraAvisosMs) : Atraso(falhasSeguidas);
            try
            {
                await sinal.AguardarAsync(espera, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Reserva, emite e conclui um lote; devolve quantos avisos foram reservados.</summary>
    public async Task<int> ProcessarLoteAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        if (!db.Database.IsRelational())
        {
            return 0;
        }

        var agora = clock.GetUtcNow().UtcDateTime;
        var reservados = await db.Database.SqlQueryRaw<AvisoReservado>(ReservarSql,
                new SqlParameter("@lote", SqlDbType.Int) { Value = Opcoes.LoteAvisos },
                new SqlParameter("@agora", SqlDbType.DateTime2) { Value = agora },
                new SqlParameter("@maximo", SqlDbType.Int) { Value = Opcoes.MaxTentativasAviso },
                new SqlParameter("@reservadoAte", SqlDbType.DateTime2) { Value = agora.AddMilliseconds(Opcoes.ReservaAvisoMs) })
            .ToListAsync(ct);
        if (reservados.Count == 0)
        {
            return 0;
        }

        var ids = reservados.Select(r => r.Id).ToList();
        var notificacoes = await db.Notificacoes.AsNoTracking().Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var destinatarios = notificacoes.Values.Select(n => n.DestinatarioId).Distinct().ToList();
        var naoLidas = await db.Notificacoes.AsNoTracking()
            .Where(n => destinatarios.Contains(n.DestinatarioId) && n.LidaEmUtc == null)
            .GroupBy(n => n.DestinatarioId)
            .Select(g => new { DestinatarioId = g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.DestinatarioId, x => x.Total, ct);

        foreach (var reservado in reservados)
        {
            if (!notificacoes.TryGetValue(reservado.Id, out var notificacao))
            {
                continue;
            }

            var aviso = (AvisoNotificacao)reservado.AvisoPendente;
            try
            {
                await EmitirAsync(notificacao, aviso, naoLidas.GetValueOrDefault(notificacao.DestinatarioId), ct);
                await ConcluirAsync(db, reservado, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await ReagendarAsync(db, reservado, exception, ct);
            }
        }

        return reservados.Count;
    }

    private async Task EmitirAsync(Notificacao notificacao, AvisoNotificacao aviso, int naoLidas, CancellationToken ct)
    {
        if (MontarAviso(notificacao, aviso, naoLidas) is not var (evento, payload))
        {
            return;
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromMilliseconds(Opcoes.TempoLimiteEnvioMs));
        await emissor.EmitirAsync(notificacao.DestinatarioId, evento, payload, limite.Token);
    }

    /// <summary>
    /// O que emitir para o estado atual da notificação: NotificacaoCriada só enquanto ela não foi lida (lida no meio do
    /// caminho, o aviso de leitura pendente substitui este) e NotificacaoLida só com a data gravada. Nulo: nada a emitir.
    /// </summary>
    public static (string Evento, object Payload)? MontarAviso(Notificacao notificacao, AvisoNotificacao aviso, int naoLidas) => aviso switch
    {
        AvisoNotificacao.Criada when notificacao.LidaEmUtc is null =>
            (NotificacoesHub.EventoNotificacaoCriada, new NotificacaoCriadaAviso(NotificacoesService.ToDto(notificacao), naoLidas)),
        AvisoNotificacao.Lida when notificacao.LidaEmUtc is { } lidaEm =>
            (NotificacoesHub.EventoNotificacaoLida, new NotificacaoLidaAviso(notificacao.Id, lidaEm, naoLidas)),
        _ => null,
    };

    /// <summary>Só limpa se o aviso ainda é o mesmo reservado: uma leitura feita durante o envio deixa o aviso de leitura pendente.</summary>
    private static Task ConcluirAsync(AlmiranteDbContext db, AvisoReservado reservado, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.notificacoes SET AvisoPendente = NULL, AvisoProximaTentativaEmUtc = NULL, AvisoTentativas = 0
            WHERE Id = {reservado.Id} AND AvisoPendente = {reservado.AvisoPendente} AND AvisoTentativas = {reservado.AvisoTentativas}
            """, ct);

    private async Task ReagendarAsync(AlmiranteDbContext db, AvisoReservado reservado, Exception exception, CancellationToken ct)
    {
        var desistiu = reservado.AvisoTentativas >= Opcoes.MaxTentativasAviso;
        var proxima = clock.GetUtcNow().UtcDateTime + Atraso(reservado.AvisoTentativas);
        if (desistiu)
        {
            logger.LogWarning(exception, "Aviso {Aviso} da notificação {NotificacaoId} abandonado após {Tentativas} tentativas; " +
                "a notificação continua disponível na consulta de pendências.", (AvisoNotificacao)reservado.AvisoPendente, reservado.Id, reservado.AvisoTentativas);
        }
        else
        {
            logger.LogWarning(exception, "Falha ao emitir o aviso {Aviso} da notificação {NotificacaoId} (tentativa {Tentativa}); nova tentativa em {Proxima:O}.",
                (AvisoNotificacao)reservado.AvisoPendente, reservado.Id, reservado.AvisoTentativas, proxima);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.notificacoes SET AvisoProximaTentativaEmUtc = {proxima}
            WHERE Id = {reservado.Id} AND AvisoPendente = {reservado.AvisoPendente} AND AvisoTentativas = {reservado.AvisoTentativas}
            """, ct);
    }

    private TimeSpan Atraso(int tentativas) => Atraso(tentativas, Opcoes);

    /// <summary>Espera depois da n-ésima falha: inicial, dobrando a cada falha, limitada ao máximo (sem laço agressivo).</summary>
    public static TimeSpan Atraso(int tentativas, NotificacoesOptions opcoes)
    {
        var expoente = Math.Clamp(tentativas - 1, 0, 30);
        var atraso = opcoes.AtrasoInicialAvisoMs * Math.Pow(2, expoente);
        return TimeSpan.FromMilliseconds(Math.Min(atraso, opcoes.AtrasoMaximoAvisoMs));
    }

    public sealed class AvisoReservado
    {
        public Guid Id { get; init; }
        public int AvisoPendente { get; init; }
        public int AvisoTentativas { get; init; }
    }
}
