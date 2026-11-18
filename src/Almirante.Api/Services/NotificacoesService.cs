using System.Linq.Expressions;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

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
        var (itens, proximoCursor) = await PaginarAsync(pendentes, query.PageSize, query.Cursor, ct);

        return new NotificacoesPendentesResponse
        {
            Items = itens,
            NaoLidas = naoLidas,
            PageSize = query.PageSize,
            ProximoCursor = proximoCursor,
        };
    }

    // Histórico da caixa inteira: mesma ordem, cursor e DTO das pendências. naoLidas conta a caixa toda, qualquer que seja o filtro.
    public async Task<HistoricoNotificacoesResponse> ListarHistoricoAsync(ListarHistoricoNotificacoesQuery query, CancellationToken ct)
    {
        var caixa = db.Notificacoes.AsNoTracking().Where(n => n.DestinatarioId == query.UsuarioId);
        var naoLidas = await caixa.CountAsync(n => n.LidaEmUtc == null, ct);

        var filtradas = query.Filtro switch
        {
            FiltroLeituraNotificacoes.Lidas => caixa.Where(n => n.LidaEmUtc != null),
            FiltroLeituraNotificacoes.NaoLidas => caixa.Where(n => n.LidaEmUtc == null),
            _ => caixa,
        };
        var (itens, proximoCursor) = await PaginarAsync(filtradas, query.PageSize, query.Cursor, ct);

        return new HistoricoNotificacoesResponse
        {
            Items = itens,
            Filtro = query.Filtro,
            NaoLidas = naoLidas,
            PageSize = query.PageSize,
            ProximoCursor = proximoCursor,
        };
    }

    private static async Task<(List<NotificacaoDto> Itens, string? ProximoCursor)> PaginarAsync(
        IQueryable<Notificacao> notificacoes, int pageSize, string? cursor, CancellationToken ct)
    {
        if (CursorNotificacoes.TentarLer(cursor, out var criadaEm, out var id))
        {
            notificacoes = notificacoes.Where(n => n.CriadaEmUtc < criadaEm || (n.CriadaEmUtc == criadaEm && n.Id.CompareTo(id) < 0));
        }

        var itens = await notificacoes
            .OrderByDescending(n => n.CriadaEmUtc)
            .ThenByDescending(n => n.Id)
            .Take(pageSize + 1)
            .Select(Projection)
            .ToListAsync(ct);

        if (itens.Count <= pageSize)
        {
            return (itens, null);
        }

        itens.RemoveAt(itens.Count - 1);
        return (itens, CursorNotificacoes.Criar(itens[^1].CriadaEmUtc, itens[^1].Id));
    }

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
                MarcarLida(notificacao, clock.GetUtcNow().UtcDateTime);
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

            return new LeituraNotificacaoResponse(ToDto(notificacao), await ContarNaoLidasAsync(command.UsuarioId, ct));
        }
    }

    public async Task<LeituraTodasNotificacoesResponse> MarcarTodasComoLidasAsync(MarcarTodasNotificacoesLidasCommand command, CancellationToken ct)
    {
        var limite = clock.GetUtcNow().UtcDateTime;
        for (var tentativa = 1; ; tentativa++)
        {
            db.ChangeTracker.Clear();
            var pendentes = await db.Notificacoes
                .Where(n => n.DestinatarioId == command.UsuarioId && n.LidaEmUtc == null && n.CriadaEmUtc <= limite)
                .ToListAsync(ct);
            if (pendentes.Count > 0)
            {
                pendentes.ForEach(n => MarcarLida(n, limite));
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

            return new LeituraTodasNotificacoesResponse(pendentes.Count, await ContarNaoLidasAsync(command.UsuarioId, ct), limite);
        }
    }

    private static void MarcarLida(Notificacao notificacao, DateTime agora)
    {
        notificacao.LidaEmUtc = agora;
        notificacao.AvisoPendente = AvisoNotificacao.Lida;
        notificacao.AvisoTentativas = 0;
        notificacao.AvisoProximaTentativaEmUtc = agora;
    }

    private Task<int> ContarNaoLidasAsync(Guid usuarioId, CancellationToken ct) =>
        db.Notificacoes.CountAsync(n => n.DestinatarioId == usuarioId && n.LidaEmUtc == null, ct);
}
