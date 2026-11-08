using Almirante.Api.Data;
using Almirante.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>Transação auditada, concorrência e conflitos comuns às escritas de formação (catálogos e vínculos).</summary>
internal static class FormacaoBanco
{
    private const int SqlUnicidadeIndice = 2601;
    private const int SqlUnicidadeConstraint = 2627;

    /// <summary>
    /// Executa a escrita numa transação com o SESSION_CONTEXT lido pelos triggers de formacao_historico. Conflito de rowversion ou de
    /// índice único (operação simultânea) vira 409 com <paramref name="conflito"/>.
    /// </summary>
    public static async Task<T> ExecutarAsync<T>(AlmiranteDbContext db, ILogger logger, Guid usuarioId, string ip, string acao,
        string? justificativa, Func<SessaoAuditoria, Task<T>> trabalho, ApiProblemException conflito, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            throw new NotSupportedException("O acompanhamento de formação exige SQL Server (transação, rowversion, trigger e SESSION_CONTEXT).");
        }

        try
        {
            return await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
            {
                await sessao.DefinirContextoFormacaoAsync(usuarioId, ip, acao, justificativa, ct);
                return await trabalho(sessao);
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiProblemException.Conflict("Conflito de concorrência.",
                "Outra operação alterou os mesmos dados. Consulte novamente e use a versão atual.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: SqlUnicidadeIndice or SqlUnicidadeConstraint })
        {
            throw conflito;
        }
    }

    public static void ConferirVersao(byte[] atual, string? versao)
    {
        if (!EventoRegras.TentarLerVersao(versao, out var informada) || !informada.AsSpan().SequenceEqual(atual))
        {
            throw ApiProblemException.Conflict("Versão desatualizada.",
                "O registro foi alterado depois da consulta. Consulte novamente e envie a versão atual.");
        }
    }

    /// <summary>Serializa as escritas no mesmo registro até o fim da transação (as regras são conferidas com o estado travado).</summary>
    public static Task BloquearAsync(AlmiranteDbContext db, string tabela, Guid id, CancellationToken ct) => tabela switch
    {
        "especialidades" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[especialidades] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "requisitos_revisoes" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[requisitos_revisoes] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "membros_classes" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[membros_classes] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "membros_especialidades" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[membros_especialidades] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(tabela), tabela, "Tabela sem trava prevista."),
    };

    public static string? Opcional(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
