using System.Data.Common;
using System.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Almirante.Api.Infrastructure;

public sealed class AppDbCredentialProvider
{
    public static readonly TimeSpan SwitchWaitLimit = TimeSpan.FromSeconds(30);

    private volatile SqlCredential? _current;
    private volatile TaskCompletionSource? _switching;

    public SqlCredential? Current => _current;

    public Task Ready => _switching?.Task ?? Task.CompletedTask;

    public IDisposable BeginSwitch()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _switching = gate;
        return new SwitchGate(this, gate);
    }

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

public sealed class AppDbCredentialInterceptor(AppDbCredentialProvider provider, TimeProvider tempo) : DbConnectionInterceptor
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
            try { await ready.WaitAsync(AppDbCredentialProvider.SwitchWaitLimit, tempo, cancellationToken); }
            catch (TimeoutException) { }
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
