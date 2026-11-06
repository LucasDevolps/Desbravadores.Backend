using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Issue #184: lançamento ativo e Pendente com vencimento anterior a hoje (data civil do clube) passa a Atrasado, de Entrada ou de Saída.
/// Roda na subida da API e depois a cada hora, ou logo após a virada do dia no fuso do clube se ela vier antes. Cada bloco trava as linhas
/// que vai alterar (UPDLOCK, READPAST): outra instância pula essas linhas e, depois do commit, elas já não são Pendente. Assim cada
/// lançamento muda e é notificado uma vez só, e um PUT simultâneo espera o bloco terminar.
/// </summary>
public sealed class LancamentosAtrasadosService(IServiceScopeFactory scopes, TimeProvider clock, DataCivil dataCivil,
    SinalAvisosNotificacoes sinal, ILogger<LancamentosAtrasadosService> logger) : BackgroundService
{
    internal const int TamanhoDoBloco = 500;
    internal static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);

    /// <summary>Folga depois da meia-noite, para a execução da virada nunca cair ainda no dia anterior.</summary>
    internal static readonly TimeSpan FolgaDaVirada = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
                if (db.Database.IsRelational())
                {
                    var total = await AtualizarAsync(db, stoppingToken);
                    if (total > 0) logger.LogInformation("{Count} lançamentos pendentes vencidos passaram a Atrasado.", total);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Falha ao marcar como Atrasado os lançamentos pendentes vencidos."); }
            await Task.Delay(ProximaExecucao(), clock, stoppingToken);
        }
    }

    internal TimeSpan ProximaExecucao()
    {
        var virada = dataCivil.AteAViradaDoDia() + FolgaDaVirada;
        return virada < Intervalo ? virada : Intervalo;
    }

    /// <summary>Processa blocos até sobrar menos que um bloco cheio; devolve quantos lançamentos passaram a Atrasado.</summary>
    internal async Task<int> AtualizarAsync(AlmiranteDbContext db, CancellationToken ct)
    {
        var total = 0;
        int alterados;
        do
        {
            alterados = await AtualizarBlocoAsync(db, ct);
            total += alterados;
        }
        while (alterados == TamanhoDoBloco);

        return total;
    }

    private async Task<int> AtualizarBlocoAsync(AlmiranteDbContext db, CancellationToken ct)
    {
        var hoje = dataCivil.Hoje();
        var agora = clock.GetUtcNow().UtcDateTime;
        var strategy = db.Database.CreateExecutionStrategy();

        // Commit incerto na retentativa: as linhas já gravadas deixaram de ser Pendente, então o bloco repetido não as vê de novo.
        var (alterados, notificacoes) = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var resultado = (Alterados: 0, Notificacoes: 0);
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var vencidos = await db.Lancamentos.FromSqlInterpolated($"""
                    SELECT TOP ({TamanhoDoBloco}) * FROM dbo.Lancamentos WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE Ativo = 1 AND Status = {(int)StatusLancamento.Pendente} AND Vencimento < {hoje}
                    ORDER BY Vencimento, Id
                    """).AsNoTracking().ToListAsync(ct);
                if (vencidos.Count > 0)
                {
                    // As linhas lidas seguem travadas até o commit: o UPDATE altera exatamente essas. O filtro repete a regra por defesa.
                    var ids = vencidos.Select(l => l.Id).ToList();
                    resultado.Alterados = await db.Lancamentos
                        .Where(l => ids.Contains(l.Id) && l.Ativo && l.Status == StatusLancamento.Pendente && l.Vencimento < hoje)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(l => l.Status, StatusLancamento.Atrasado)
                            .SetProperty(l => l.DataAtualizacao, agora)
                            .SetProperty(l => l.AtualizadoPorUsuarioId, (Guid?)null), ct);
                    resultado.Notificacoes = await NotificarAsync(db, vencidos, agora, ct);
                }

                await tx.CommitAsync(ct);
            }

            return resultado;
        });

        if (notificacoes > 0)
        {
            sinal.Sinalizar();
        }

        return alterados;
    }

    /// <summary>
    /// Uma notificação por débito de membro, na mesma transação da mudança de status. Um aviso já criado para o mesmo vencimento (o
    /// lançamento voltou a Pendente sem prorrogação e venceu de novo) não é repetido.
    /// </summary>
    private static async Task<int> NotificarAsync(AlmiranteDbContext db, List<Lancamento> vencidos, DateTime agora, CancellationToken ct)
    {
        var debitos = vencidos.Where(NotificacoesFabrica.EhDebito).ToList();
        if (debitos.Count == 0)
        {
            return 0;
        }

        var ids = debitos.Select(l => (Guid?)l.Id).ToList();
        var existentes = (await db.Notificacoes.AsNoTracking()
                .Where(n => n.Tipo == TipoNotificacao.DebitoAtrasado && ids.Contains(n.LancamentoId))
                .Select(n => n.ChaveOrigem)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var novas = debitos.Select(l => NotificacoesFabrica.DebitoAtrasado(l, agora))
            .Where(n => !existentes.Contains(n.ChaveOrigem))
            .ToList();
        db.Notificacoes.AddRange(novas);
        await db.SaveChangesAsync(ct);
        return novas.Count;
    }
}
