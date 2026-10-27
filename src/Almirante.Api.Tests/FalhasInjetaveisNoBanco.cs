using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Almirante.Api.Tests;

/// <summary>Falhas que só aparecem sob concorrência real; aqui são injetadas na hora certa para o resultado ser determinístico.</summary>
public sealed class FalhasInjetaveisNoBanco : SaveChangesInterceptor
{
    private (Func<Exception> Excecao, Func<Task>? Antes)? _proximoSave;
    private Func<string, bool>? _comandoSemEfeito;

    /// <summary>O próximo SaveChanges falha com a exceção; <paramref name="antes"/> roda primeiro (ex.: a outra operação que "ganhou" a corrida).</summary>
    public void FalharProximoSave(Func<Exception> excecao, Func<Task>? antes = null) => _proximoSave = (excecao, antes);

    /// <summary>O próximo comando que passar no filtro roda sem efeito (como se outra transação tivesse alterado a linha antes).</summary>
    public void AnularProximoComando(Func<string, bool> filtro) => _comandoSemEfeito = filtro;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // O gravador da auditoria HTTP salva em segundo plano com os mesmos interceptores: a falha armada é só da operação de negócio.
        if (_proximoSave is { } falha && eventData.Context is { } contexto
            && contexto.ChangeTracker.Entries().Any(e => e.Entity is not Almirante.Api.Entities.AuditoriaHttp))
        {
            _proximoSave = null;
            if (falha.Antes is not null) await falha.Antes();
            throw falha.Excecao();
        }
        return result;
    }

    public sealed class Comandos(FalhasInjetaveisNoBanco falhas) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (falhas._comandoSemEfeito is { } filtro && filtro(command.CommandText))
            {
                falhas._comandoSemEfeito = null;
                command.CommandText = "SELECT 1 WHERE 1 = 0";
            }
            return ValueTask.FromResult(result);
        }
    }
}
