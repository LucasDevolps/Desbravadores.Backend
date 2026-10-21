using System.Linq.Expressions;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>Consulta e leitura das notificações do próprio usuário: toda consulta filtra pelo destinatário vindo do token.</summary>
public sealed class NotificacoesService(AlmiranteDbContext db, TimeProvider clock, SinalAvisosNotificacoes sinal)
{
    public const int PageSizePadrao = 20;
    public const int PageSizeMaximo = 50;

    private const int TentativasDeLeitura = 3;

    public static readonly Expression<Func<Notificacao, NotificacaoDto>> Projection = n => new NotificacaoDto
    {
        Id = n.Id,
        Tipo = n.Tipo,
        Titulo = n.Titulo,
        Mensagem = n.Mensagem,
        CriadaEmUtc = n.CriadaEmUtc,
        LidaEmUtc = n.LidaEmUtc,
        EventoId = n.EventoId,
        LancamentoId = n.LancamentoId,
    };

    public static readonly Func<Notificacao, NotificacaoDto> ToDto = Projection.Compile();

    public async Task<NotificacoesPendentesResponse> ListarPendentesAsync(ListarNotificacoesPendentesQuery query, CancellationToken ct)
    {
        var pendentes = db.Notificacoes.AsNoTracking().Where(n => n.DestinatarioId == query.UsuarioId && n.LidaEmUtc == null);
        var naoLidas = await pendentes.CountAsync(ct);

        var pagina = pendentes;
        if (CursorNotificacoes.TentarLer(query.Cursor, out var criadaEm, out var id))
        {
            pagina = pagina.Where(n => n.CriadaEmUtc < criadaEm || (n.CriadaEmUtc == criadaEm && n.Id.CompareTo(id) < 0));
        }

        var itens = await pagina
            .OrderByDescending(n => n.CriadaEmUtc)
            .ThenByDescending(n => n.Id)
            .Take(query.PageSize + 1)
            .Select(Projection)
            .ToListAsync(ct);

        var temMais = itens.Count > query.PageSize;
        if (temMais)
        {
            itens.RemoveAt(itens.Count - 1);
        }

        return new NotificacoesPendentesResponse
        {
            Items = itens,
            NaoLidas = naoLidas,
            PageSize = query.PageSize,
            ProximoCursor = temMais ? CursorNotificacoes.Criar(itens[^1].CriadaEmUtc, itens[^1].Id) : null,
        };
    }

    /// <summary>
    /// Grava a primeira leitura e o aviso para as outras conexões do usuário na mesma instrução. Repetir (ou ler em paralelo)
    /// devolve a data já gravada: LidaEmUtc é token de concorrência, então só uma leitura vence e as demais releem.
    /// Nula para notificação inexistente ou de outro destinatário (mesma resposta nos dois casos).
    /// </summary>
    public async Task<LeituraNotificacaoResponse?> MarcarComoLidaAsync(MarcarNotificacaoLidaCommand command, CancellationToken ct)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            db.ChangeTracker.Clear();
            var notificacao = await db.Notificacoes
                .SingleOrDefaultAsync(n => n.Id == command.Id && n.DestinatarioId == command.UsuarioId, ct);
            if (notificacao is null)
            {
                return null;
            }

            if (notificacao.LidaEmUtc is null)
            {
                var agora = clock.GetUtcNow().UtcDateTime;
                notificacao.LidaEmUtc = agora;
                notificacao.AvisoPendente = AvisoNotificacao.Lida;
                notificacao.AvisoTentativas = 0;
                notificacao.AvisoProximaTentativaEmUtc = agora;
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException) when (tentativa < TentativasDeLeitura)
                {
                    continue;
                }

                sinal.Sinalizar();
            }

            var naoLidas = await db.Notificacoes.CountAsync(n => n.DestinatarioId == command.UsuarioId && n.LidaEmUtc == null, ct);
            return new LeituraNotificacaoResponse(ToDto(notificacao), naoLidas);
        }
    }
}
