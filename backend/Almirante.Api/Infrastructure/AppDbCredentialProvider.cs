using System.Data.Common;
using System.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Almirante.Api.Infrastructure;

// Guarda em memória a credencial SQL atual do usuário da aplicação. A senha nunca é persistida em
// disco nem em configuração: é gerada e trocada por DbCredentialManager (a cada startup e
// periodicamente) e lida aqui a cada nova conexão física.
public sealed class AppDbCredentialProvider
{
    // Tempo máximo que uma abertura de conexão espera uma troca de credencial em andamento.
    public static readonly TimeSpan SwitchWaitLimit = TimeSpan.FromSeconds(30);

    private volatile SqlCredential? _current;
    private volatile TaskCompletionSource? _switching;

    public SqlCredential? Current => _current;

    // Completa quando não há troca de senha em andamento. Enquanto o ALTER USER ainda não terminou (ou a
    // credencial nova ainda não foi publicada), abrir uma conexão com a credencial "atual" usaria uma senha
    // prestes a ser invalidada; o interceptor espera aqui em vez de falhar com 18456.
    public Task Ready => _switching?.Task ?? Task.CompletedTask;

    // Fecha o portão de novas conexões até o Dispose. Só um chamador por vez (DbCredentialManager serializa).
    public IDisposable BeginSwitch()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _switching = gate;
        return new SwitchGate(this, gate);
    }

    // Publica a credencial nova e devolve a anterior (para o chamador esvaziar o pool dela).
    public SqlCredential? Set(string user, string password)
    {
        var secure = new SecureString();
        foreach (var c in password) secure.AppendChar(c);
        secure.MakeReadOnly();
        var previous = _current;
        _current = new SqlCredential(user, secure);
        return previous;
    }

    private sealed class SwitchGate(AppDbCredentialProvider owner, TaskCompletionSource gate) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._switching, gate)) owner._switching = null;
            gate.TrySetResult();
        }
    }
}

// Aplica a credencial vigente antes de abrir cada conexão do EF Core. Como a connection string não
// contém usuário/senha, isso permite trocar a senha em execução sem reiniciar nem recriar o
// DbContext (conexões já abertas no pool continuam válidas; as novas usam a senha nova).
public sealed class AppDbCredentialInterceptor(AppDbCredentialProvider provider) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (!provider.Ready.IsCompleted) provider.Ready.Wait(AppDbCredentialProvider.SwitchWaitLimit);
        Apply(connection);
        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        var ready = provider.Ready;
        if (!ready.IsCompleted)
        {
            try { await ready.WaitAsync(AppDbCredentialProvider.SwitchWaitLimit, cancellationToken); }
            catch (TimeoutException) { /* segue com a credencial vigente: no pior caso a abertura falha como antes */ }
        }
        Apply(connection);
        return result;
    }

    private void Apply(DbConnection connection)
    {
        if (connection is not SqlConnection sql) return;
        sql.Credential = provider.Current
            ?? throw new InvalidOperationException("Credencial do usuário da aplicação ainda não foi provisionada (DbCredentialManager).");
    }
}
