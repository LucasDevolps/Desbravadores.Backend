using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentoGeralService(AlmiranteDbContext db, TimeProvider clock, SinalAvisosNotificacoes sinal)
{
    public async Task<RegistrarLancamentoResult> RegistrarAsync(RegistrarLancamentoRequest request, CancellationToken ct)
    {
        var key = request.IdempotencyKey!.Trim();
        var hash = CalcularHash(request);
        var existing = await db.LancamentosOperacoes.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            return ResultadoExistente(existing, hash);
        }

        var membros = await db.Usuarios.AsNoTracking().Where(u => u.Ativo).Select(u => u.Id).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var operationId = Guid.NewGuid();
        var entities = membros.Select(id => LancamentoMapping.Criar(request, id, operationId, now)).ToList();
        var operation = NovaOperacao(request, operationId, key, hash, membros.Count, entities.Count, now);
        db.LancamentosOperacoes.Add(operation);
        db.Lancamentos.AddRange(entities);
        // Cada membro contemplado recebe a própria notificação, ligada ao lançamento dele, no mesmo SaveChanges (atômico).
        var notificacoes = NotificacoesFabrica.ParaDebitos(entities, now);
        db.Notificacoes.AddRange(notificacoes);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.LancamentosOperacoes.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (existing is null)
            {
                throw;
            }

            return ResultadoExistente(existing, hash);
        }

        if (notificacoes.Count > 0)
        {
            sinal.Sinalizar();
        }

        return new(RegistrarLancamentoOutcome.GeralCriado, null, CriarResposta(operation));
    }

    /// <summary>
    /// Hash da operação idempotente. O do modo geral não mudou (operações antigas continuam reconhecidas); o individual inclui
    /// o membro, então a mesma chave usada no outro modo, ou para outro membro, é conflito.
    /// </summary>
    public static string CalcularHash(RegistrarLancamentoRequest request)
    {
        var canonical = string.Join('|',
            request.Finalidade,
            request.Descricao?.Trim() ?? "",
            request.Categoria,
            request.TipoFluxo == TipoFluxoLancamento.Saida ? "Despesa" : "Entrada",
            request.Valor.ToString("F2", CultureInfo.InvariantCulture),
            request.Vencimento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (!request.AplicarATodosOsMembros)
        {
            canonical = $"individual|{request.MembroId:D}|{canonical}";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static LancamentoOperacao NovaOperacao(RegistrarLancamentoRequest request, Guid id, string key, string hash,
        int usuariosProcessados, int lancamentosCriados, DateTime now) => new()
    {
        Id = id,
        IdempotencyKey = key,
        RequestHash = hash,
        Finalidade = request.Finalidade,
        Descricao = request.Descricao,
        Categoria = request.Categoria,
        TipoFluxo = request.TipoFluxo,
        Valor = request.Valor,
        Vencimento = request.Vencimento,
        UsuariosProcessados = usuariosProcessados,
        LancamentosCriados = lancamentosCriados,
        CriadoPorUsuarioId = request.UsuarioSolicitanteId,
        CriadoEmUtc = now,
    };

    private static RegistrarLancamentoResult ResultadoExistente(LancamentoOperacao op, string hash) => op.RequestHash == hash
        ? new(RegistrarLancamentoOutcome.GeralReutilizado, null, CriarResposta(op))
        : new(RegistrarLancamentoOutcome.GeralConflitoIdempotencia, null, null);

    private static LancamentoGeralResponse CriarResposta(LancamentoOperacao op) => new()
    {
        OperacaoId = op.Id,
        UsuariosProcessados = op.UsuariosProcessados,
        LancamentosCriados = op.LancamentosCriados,
        DataHoraUtc = op.CriadoEmUtc,
    };
}
