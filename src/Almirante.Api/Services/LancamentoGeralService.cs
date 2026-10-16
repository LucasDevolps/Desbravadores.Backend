using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class LancamentoGeralService(AlmiranteDbContext db, TimeProvider clock)
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

        var membros = await db.Usuarios.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var operationId = Guid.NewGuid();
        var entities = membros.Select(id => LancamentoMapping.Criar(request, id, operationId, now)).ToList();
        var operation = new LancamentoOperacao
        {
            Id = operationId,
            IdempotencyKey = key,
            RequestHash = hash,
            Finalidade = request.Finalidade,
            Descricao = request.Descricao,
            Categoria = request.Categoria,
            TipoFluxo = request.TipoFluxo,
            Valor = request.Valor,
            Vencimento = request.Vencimento,
            UsuariosProcessados = membros.Count,
            LancamentosCriados = entities.Count,
            CriadoPorUsuarioId = request.UsuarioSolicitanteId,
            CriadoEmUtc = now,
        };
        db.LancamentosOperacoes.Add(operation);
        db.Lancamentos.AddRange(entities);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // O índice único resolve a corrida: reutilize a operação que venceu a gravação.
            // Sem uma operação persistida, propague a falha original.
            db.ChangeTracker.Clear();
            existing = await db.LancamentosOperacoes.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (existing is null)
            {
                throw;
            }

            return ResultadoExistente(existing, hash);
        }
        return new(RegistrarLancamentoOutcome.GeralCriado, null, CriarResposta(operation));
    }

    private static string CalcularHash(RegistrarLancamentoRequest request)
    {
        // Preserve o formato histórico do hash: Saida era persistido como "Despesa".
        // Mudar esse texto invalidaria o replay das chaves criadas antes da migração.
        var canonical = string.Join('|',
            request.Finalidade,
            request.Descricao?.Trim() ?? "",
            request.Categoria,
            request.TipoFluxo == TipoFluxoLancamento.Saida ? "Despesa" : "Entrada",
            request.Valor.ToString("F2", CultureInfo.InvariantCulture),
            request.Vencimento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

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
