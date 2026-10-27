using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

public interface IRepositorioAuditoriaHttp
{
    /// <summary>Insere os registros que ainda não existem (pelo Id): repetir o mesmo lote não duplica nada.</summary>
    Task InserirAsync(IReadOnlyList<AuditoriaHttp> registros, CancellationToken cancellationToken);
}

/// <summary>
/// Unidade de trabalho própria (escopo e DbContext novos a cada lote): nunca reaproveita o contexto de uma requisição, então rollback ou
/// falha do negócio não desfaz nem contamina a auditoria.
/// </summary>
public sealed class RepositorioAuditoriaHttp(IServiceScopeFactory escopos) : IRepositorioAuditoriaHttp
{
    public async Task InserirAsync(IReadOnlyList<AuditoriaHttp> registros, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var ids = registros.Select(r => r.Id).ToList();
        var gravados = (await db.AuditoriasHttp.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(cancellationToken))
            .ToHashSet();
        var novos = registros.Where(r => gravados.Add(r.Id)).ToList();
        if (novos.Count == 0)
        {
            return;
        }

        db.AuditoriasHttp.AddRange(novos);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Leva o diário da auditoria HTTP para o banco (issue #158): a cada <see cref="AuditoriaHttpOptions.IntervaloGravacaoMs"/> fecha o
/// segmento ativo, insere os segmentos pendentes em lotes idempotentes e só então apaga cada arquivo; depois esvazia a reserva em
/// memória. Banco indisponível: nada é apagado, e a espera dobra até <see cref="AuditoriaHttpOptions.EsperaMaximaSegundos"/>. Registro
/// recusado de forma permanente (constraint, tamanho) é isolado em "rejeitados" para não travar os demais. No encerramento tenta uma
/// última gravação curta; o que sobrar fica em disco para o próximo início.
/// </summary>
public sealed class GravadorAuditoriaHttp(DiarioAuditoriaHttp diario, IRepositorioAuditoriaHttp repositorio, IOptions<AuditoriaHttpOptions> opcoes,
    TimeProvider tempo, ILogger<GravadorAuditoriaHttp> logger) : BackgroundService
{
    private static readonly HashSet<int> ErrosPermanentes = [547, 2601, 2627, 2628, 8152, 241, 242, 245, 515, 8114, 50158];

    private readonly SemaphoreSlim _ciclo = new(1, 1);
    private DateTimeOffset? _falhandoDesde;

    /// <summary>Desde quando o banco recusa a gravação (nulo enquanto ela funciona).</summary>
    public DateTimeOffset? FalhandoDesde => _falhandoDesde;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMilliseconds(opcoes.Value.IntervaloGravacaoMs);
        var esperaMaxima = TimeSpan.FromSeconds(Math.Max(opcoes.Value.EsperaMaximaSegundos, intervalo.TotalSeconds));
        var espera = intervalo;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(espera, tempo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool gravou;
            try
            {
                gravou = await GravarPendentesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Falha inesperada no gravador da auditoria HTTP ({Tipo}); os pendentes continuam em disco.", ex.GetType().FullName);
                gravou = false;
            }

            espera = gravou ? intervalo : TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, esperaMaxima.Ticks));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await GravarPendentesAsync(prazo.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Última gravação da auditoria HTTP no encerramento não terminou ({Tipo}); os pendentes ficam em {Diretorio}.",
                ex.GetType().Name, diario.Diretorio);
        }
    }

    /// <summary>Grava agora tudo o que estiver pendente. Falso quando o banco falhou (os registros continuam no diário ou na reserva).</summary>
    public async Task<bool> GravarPendentesAsync(CancellationToken cancellationToken)
    {
        await _ciclo.WaitAsync(cancellationToken);
        try
        {
            await diario.RotacionarAsync();
            diario.InformarDescartes();
            foreach (var segmento in diario.SegmentosPendentes())
            {
                using (segmento)
                {
                    if (segmento.LinhasInvalidas > 0)
                    {
                        logger.LogWarning("Segmento {Arquivo} da auditoria HTTP tem {Linhas} linhas incompletas (escrita interrompida); ignoradas.",
                            Path.GetFileName(segmento.Caminho), segmento.LinhasInvalidas);
                    }

                    if (!await InserirAsync(segmento.Registros, cancellationToken))
                    {
                        return false;
                    }

                    segmento.Concluir();
                }
            }

            while (diario.RetirarDaReserva(opcoes.Value.TamanhoLote) is { Count: > 0 } reserva)
            {
                if (!await InserirAsync(reserva, cancellationToken))
                {
                    await diario.DevolverAsync(reserva);
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _ciclo.Release();
        }
    }

    private async Task<bool> InserirAsync(IReadOnlyList<AuditoriaHttp> registros, CancellationToken cancellationToken)
    {
        foreach (var lote in registros.Chunk(opcoes.Value.TamanhoLote))
        {
            try
            {
                await repositorio.InserirAsync(lote, cancellationToken);
            }
            catch (Exception ex) when (Permanente(ex))
            {
                foreach (var registro in lote)
                {
                    try
                    {
                        await repositorio.InserirAsync([registro], cancellationToken);
                    }
                    catch (Exception recusa) when (Permanente(recusa))
                    {
                        logger.LogError("Registro de auditoria HTTP {Id} recusado pelo banco (erro SQL {Numero}); guardado em {Pasta}.",
                            registro.Id, NumeroSql(recusa), DiarioAuditoriaHttp.PastaRejeitados);
                        diario.Rejeitar(registro);
                    }
                    catch (Exception falha) when (!cancellationToken.IsCancellationRequested)
                    {
                        return Falhou(falha);
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return Falhou(ex);
            }
        }

        if (_falhandoDesde is { } desde)
        {
            _falhandoDesde = null;
            logger.LogInformation("Auditoria HTTP voltou a gravar no banco (falhava desde {Desde:O}).", desde);
        }

        return true;
    }

    private bool Falhou(Exception ex)
    {
        if (_falhandoDesde is null)
        {
            _falhandoDesde = tempo.GetUtcNow();
            logger.LogWarning("Auditoria HTTP não conseguiu gravar no banco ({Tipo}, erro SQL {Numero}); os registros ficam em {Diretorio} e " +
                "serão reenviados.", ex.GetType().Name, NumeroSql(ex), diario.Diretorio);
        }

        return false;
    }

    private static bool Permanente(Exception ex) => NumeroSql(ex) is { } numero && ErrosPermanentes.Contains(numero);

    private static int? NumeroSql(Exception ex)
    {
        for (var atual = ex; atual is not null; atual = atual.InnerException)
        {
            if (atual is SqlException sql)
            {
                return sql.Number;
            }
        }

        return null;
    }
}
