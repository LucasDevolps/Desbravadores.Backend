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
    private volatile SqlCredential? _current;

    public SqlCredential? Current => _current;

    public void Set(string user, string password)
    {
        var secure = new SecureString();
        foreach (var c in password) secure.AppendChar(c);
        secure.MakeReadOnly();
        _current = new SqlCredential(user, secure);
    }
}

// Aplica a credencial vigente antes de abrir cada conexão do EF Core. Como a connection string não
// contém usuário/senha, isso permite trocar a senha em execução sem reiniciar nem recriar o
// DbContext (conexões já abertas no pool continuam válidas; as novas usam a senha nova).
public sealed class AppDbCredentialInterceptor(AppDbCredentialProvider provider) : DbConnectionInterceptor
{
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Apply(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbConnection connection)
    {
        if (connection is not SqlConnection sql) return;
        sql.Credential = provider.Current
            ?? throw new InvalidOperationException("Credencial do usuário da aplicação ainda não foi provisionada (DbCredentialManager).");
    }
}
