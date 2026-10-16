using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Almirante.Api.Dtos;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

// Apoio dos testes SQL Server de /api/Eventos que controlam intercalamentos e falhas de forma determinística
// (sem Task.Delay como prova): estratégia de retry equivalente à do Aspire, falha injetada no commit e pausas
// em comandos SQL específicos. Nada disto existe no código de produção.

// Falha "transitória" só de teste; a estratégia abaixo a trata como retentável.
public sealed class FalhaTransitoriaDeTeste(string mensagem) : Exception(mensagem);

// Mesma estratégia do Aspire (SqlServerRetryingExecutionStrategy: AddSqlServerDbContext liga EnableRetryOnFailure),
// com atraso curto e a falha de teste também considerada retentável. UseSqlServer(...) sem isso NÃO retenta nada.
public sealed class EstrategiaComRetryDeTeste(ExecutionStrategyDependencies dependencies)
    : SqlServerRetryingExecutionStrategy(dependencies, 3, TimeSpan.FromMilliseconds(20), null)
{
    protected override bool ShouldRetryOn(Exception exception) => exception is FalhaTransitoriaDeTeste || base.ShouldRetryOn(exception);
}

public enum MomentoDaFalha { AntesDoCommit, DepoisDoCommit }

// Lança uma falha transitória no commit: antes (a transação é revertida) ou depois (o commit foi efetivado no
// banco, mas a confirmação "não chegou" à aplicação).
public sealed class FalhaDeCommitInterceptor : DbTransactionInterceptor
{
    private int _restantes;
    private MomentoDaFalha _momento;
    private int _transacoesIniciadas;
    private int _commitsEfetivados;

    public int TransacoesIniciadas => Volatile.Read(ref _transacoesIniciadas);
    public int CommitsEfetivados => Volatile.Read(ref _commitsEfetivados);

    public void Armar(MomentoDaFalha momento, int vezes = 1)
    {
        _momento = momento;
        Volatile.Write(ref _restantes, vezes);
        Interlocked.Exchange(ref _transacoesIniciadas, 0);
        Interlocked.Exchange(ref _commitsEfetivados, 0);
    }

    public void Desarmar() => Volatile.Write(ref _restantes, 0);

    private bool Consumir(MomentoDaFalha momento)
    {
        if (_momento != momento) return false;
        while (true)
        {
            var atual = Volatile.Read(ref _restantes);
            if (atual <= 0) return false;
            if (Interlocked.CompareExchange(ref _restantes, atual - 1, atual) == atual) return true;
        }
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _transacoesIniciadas);
        return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Consumir(MomentoDaFalha.AntesDoCommit)) throw new FalhaTransitoriaDeTeste("falha transitória antes de efetivar o commit");
        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commitsEfetivados);
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        if (Consumir(MomentoDaFalha.DepoisDoCommit)) throw new FalhaTransitoriaDeTeste("commit efetivado, confirmação perdida");
    }
}

// Segura o PRIMEIRO comando SQL que satisfaça o predicado até o teste liberar (com teto de tempo), para forçar
// um intercalamento exato entre duas requisições.
public sealed class PausaDeComandoInterceptor : DbCommandInterceptor
{
    private volatile Pausa? _ativa;

    public Pausa Armar(Func<string, bool> predicado) => _ativa = new Pausa(predicado);

    public sealed class Pausa(Func<string, bool> predicado)
    {
        private readonly TaskCompletionSource _atingida = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _liberada = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disparada;

        public Task Atingida => _atingida.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Liberar() => _liberada.TrySetResult();

        internal async Task AguardarSeAplicavelAsync(string sql)
        {
            if (!predicado(sql) || Interlocked.Exchange(ref _disparada, 1) == 1) return;
            _atingida.TrySetResult();
            await _liberada.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (_ativa is { } p) await p.AguardarSeAplicavelAsync(command.CommandText);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_ativa is { } p) await p.AguardarSeAplicavelAsync(command.CommandText);
        return result;
    }
}

// Helpers comuns às classes de teste (cada uma tem a própria fábrica/banco).
public static class EventosSqlSupport
{
    public static async Task<List<Guid>> MembrosAsync(SqlServerApiFactory factory, int quantidade)
    {
        var lista = new List<Guid>();
        for (var i = 0; i < quantidade; i++)
            lista.Add((await TestHelpers.AddUsuarioAsync(factory, $"Membro evento {Guid.NewGuid():N}", "DS")).Id);
        return lista;
    }

    public static async Task<EventoDto> CriarAsync(HttpClient client, object? membros, Guid? referencia = null, HttpStatusCode esperado = HttpStatusCode.Created,
        string? chave = null, DateOnly? data = null, string local = "Parque Ibirapuera")
    {
        var response = await EventosTestKit.PostAsync(client, EventosTestKit.Corpo(membros, data, local, referencia: referencia), chave);
        Assert.True(esperado == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await EventosTestKit.LerAsync(response);
    }

    public static Dictionary<string, object?> Editar(EventoDto e, object? membros, decimal? transporte = null, string? local = null,
        DateOnly? data = null, string? motivo = null, decimal? alimentacao = null, bool? individual = null, bool? gratis = null, decimal? seguro = null) =>
        EventosTestKit.Corpo(membros, data ?? e.DataEvento, local ?? e.Local, transporte ?? e.Transporte.Valor, gratis ?? e.Transporte.EhGratis,
            alimentacao ?? e.Alimentacao.Valor, individual ?? e.Alimentacao.Individual, seguro ?? e.SeguroObrigatorio,
            versao: e.Versao, motivo: motivo);

    public static async Task<T?> EscalarAsync<T>(SqlServerApiFactory factory, string sql, params (string, object)[] parametros)
    {
        await using var conexao = new SqlConnection(factory.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valor) in parametros) comando.Parameters.AddWithValue(nome, valor);
        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? default : (T)resultado;
    }

    // Quantas sessões estão ESPERANDO um lock de aplicação (sp_getapplock) neste banco. Lê pela identidade do
    // harness (VIEW SERVER STATE); é a prova de que uma requisição está mesmo bloqueada pela coordenação.
    public static Task<int> EsperasDeApplockAsync(SqlServerApiFactory factory) => ContarApplocksAsync(factory, apenasEsperando: true);

    public static async Task<int> ContarApplocksAsync(SqlServerApiFactory factory, bool apenasEsperando)
    {
        var banco = new SqlConnectionStringBuilder(factory.ConnectionString).InitialCatalog;
        await using var conexao = new SqlConnection(SqlIdentityEnvironment.HarnessConnectionString);
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = N'APPLICATION' AND (@espera = 0 OR request_status = N'WAIT') AND resource_database_id = DB_ID(@banco)";
        comando.Parameters.AddWithValue("@espera", apenasEsperando ? 1 : 0);
        comando.Parameters.AddWithValue("@banco", banco);
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    // Espera (com teto) até que a tarefa termine OU exista alguém esperando um applock: em qualquer dos casos o
    // intercalamento já se definiu e o teste pode liberar a operação pausada.
    public static async Task AguardarConclusaoOuBloqueioAsync(SqlServerApiFactory factory, Task tarefa, TimeSpan? limite = null)
    {
        var fim = DateTime.UtcNow + (limite ?? TimeSpan.FromSeconds(20));
        while (!tarefa.IsCompleted && DateTime.UtcNow < fim)
        {
            if (await EsperasDeApplockAsync(factory) > 0) return;
            await Task.Delay(50);
        }
    }

    public static async Task AguardarBloqueioAsync(SqlServerApiFactory factory, TimeSpan? limite = null)
    {
        var fim = DateTime.UtcNow + (limite ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < fim)
        {
            if (await EsperasDeApplockAsync(factory) > 0) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Nenhuma requisição ficou esperando o lock do passeio.");
    }

    public static Task<HttpResponseMessage> PagarAsync(HttpClient client, Guid lancamentoId, string status = "Pago") =>
        client.PutAsJsonAsync($"/api/Lancamentos/{lancamentoId}", new { status });

    public static SqlServerApiFactory FabricaComRetry(Action<DbContextOptionsBuilder>? configureDb = null, Action<IServiceCollection>? services = null,
        IDictionary<string, string?>? overrides = null) =>
        new(overrides, configureDb, sql => sql.ExecutionStrategy(d => new EstrategiaComRetryDeTeste(d)), services);
}
