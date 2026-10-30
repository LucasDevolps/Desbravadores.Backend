using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

/// <summary>
/// Recuperação de senha sem login por atendimento assistido (issue #159). O pedido público não comprova identidade: só entra numa fila
/// consultada por quem a matriz permite atender, sem notificar ninguém. O responsável confirma a identidade pessoalmente ou por procedimento
/// independente já estabelecido e, reautenticado com a própria senha, troca a senha diretamente ou emite uma autorização de uso único para o
/// titular escolher a senha. A recuperação automática por e-mail está desabilitada: não há provedor nem canal comprovado.
/// </summary>
public sealed class RecuperacaoSenhaService(AlmiranteDbContext db, ContasSenha contas, TimeProvider clock, IOptions<SenhasOptions> options,
    ILogger<RecuperacaoSenhaService> logger)
{
    public const string EncerramentoExpirada = "expirada";
    public const int SolicitacoesMaximas = 1_000;

    private const int SqlUnicidadeIndice = 2601;
    private const int SqlUnicidadeConstraint = 2627;

    /// <summary>
    /// Guarda o pedido só para conta ativa, com cargo ativo e que alguém possa atender; repetição aberta só soma contagem (deduplicação).
    /// Conta inexistente, inativa ou que ninguém atende não deixa rastro. Nunca altera senha, sessões ou bloqueio de login, e o chamador
    /// responde sempre o mesmo 202 genérico.
    /// </summary>
    public async Task SolicitarAsync(SolicitarRecuperacaoSenhaRequest request, CancellationToken ct)
    {
        var agora = clock.GetUtcNow().UtcDateTime;
        var email = UsuariosService.NormalizarEmail(request.Email!);
        var conta = await db.Usuarios.AsNoTracking().Where(u => u.EmailNormalizado == email)
            .Select(u => new { u.Id, u.Ativo, CargoAtivo = u.Cargo!.Ativo, u.Cargo.Role })
            .SingleOrDefaultAsync(ct);
        if (conta is null || !conta.Ativo || !conta.CargoAtivo || !PermissoesSenha.PodeSerAlvo(conta.Role))
        {
            return;
        }

        try
        {
            var aberta = await db.RecuperacoesSenha.SingleOrDefaultAsync(r => r.UsuarioId == conta.Id
                && (r.Status == StatusRecuperacaoSenha.Pendente || r.Status == StatusRecuperacaoSenha.Autorizada), ct);
            if (aberta is not null && aberta.ExpiraEmUtc > agora)
            {
                aberta.Solicitacoes = Math.Min(aberta.Solicitacoes + 1, SolicitacoesMaximas);
                aberta.UltimaSolicitacaoEmUtc = agora;
                if (aberta.Status == StatusRecuperacaoSenha.Pendente)
                {
                    aberta.ExpiraEmUtc = agora.AddDays(options.Value.ValidadeSolicitacaoDias);
                }
                await db.SaveChangesAsync(ct);
                return;
            }

            if (aberta is not null)
            {
                Encerrar(aberta, EncerramentoExpirada, agora);
                await db.SaveChangesAsync(ct);
            }

            db.RecuperacoesSenha.Add(new RecuperacaoSenha
            {
                Id = Guid.NewGuid(),
                UsuarioId = conta.Id,
                Origem = OrigemRecuperacaoSenha.SolicitacaoPublica,
                Status = StatusRecuperacaoSenha.Pendente,
                SolicitadaEmUtc = agora,
                UltimaSolicitacaoEmUtc = agora,
                Solicitacoes = 1,
                IpSolicitacao = request.Ip,
                ExpiraEmUtc = agora.AddDays(options.Value.ValidadeSolicitacaoDias),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ViolouUnicidade(ex.InnerException))
        {
            // Pedido paralelo da mesma conta gravou primeiro: o registro aberto já existe, que é o resultado desejado.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Pedidos abertos e não expirados de contas ativas cujo cargo o solicitante (cargo relido do banco) pode atender.</summary>
    public async Task<RecuperacoesSenhaPendentesResponse> ListarPendentesAsync(ListarRecuperacoesSenhaPendentesQuery query, CancellationToken ct)
    {
        var agora = clock.GetUtcNow().UtcDateTime;
        var solicitante = await contas.CarregarAsync(query.SolicitanteId, ct);
        var alvos = solicitante is { Utilizavel: true } ? PermissoesSenha.AlvosPermitidos(solicitante.Role) : [];
        if (alvos.Count == 0)
        {
            throw ContasSenha.AcessoNegado();
        }

        var abertas =
            from r in db.RecuperacoesSenha.AsNoTracking()
            join u in db.Usuarios.AsNoTracking() on r.UsuarioId equals u.Id
            where (r.Status == StatusRecuperacaoSenha.Pendente || r.Status == StatusRecuperacaoSenha.Autorizada) && r.ExpiraEmUtc > agora
                  && u.Ativo && u.Cargo!.Ativo && alvos.Contains(u.Cargo.Role)
            select new { r, u };
        if (CursorNotificacoes.TentarLer(query.Cursor, out var solicitadaEm, out var id))
        {
            abertas = abertas.Where(x => x.r.SolicitadaEmUtc < solicitadaEm || (x.r.SolicitadaEmUtc == solicitadaEm && x.r.Id.CompareTo(id) < 0));
        }

        var itens = await abertas
            .OrderByDescending(x => x.r.SolicitadaEmUtc)
            .ThenByDescending(x => x.r.Id)
            .Take(query.PageSize + 1)
            .Select(x => new RecuperacaoSenhaPendenteDto
            {
                Id = x.r.Id,
                UsuarioId = x.u.Id,
                Nome = x.u.Nome,
                Cargo = new MeCargoDto { Id = x.u.Cargo!.Id, Nome = x.u.Cargo.Nome, Role = x.u.Cargo.Role },
                Origem = x.r.Origem,
                Status = x.r.Status,
                SolicitadaEmUtc = x.r.SolicitadaEmUtc,
                UltimaSolicitacaoEmUtc = x.r.UltimaSolicitacaoEmUtc,
                Solicitacoes = x.r.Solicitacoes,
                ExpiraEmUtc = x.r.ExpiraEmUtc,
                AutorizacaoExpiraEmUtc = x.r.TokenExpiraEmUtc,
            })
            .ToListAsync(ct);

        var temMais = itens.Count > query.PageSize;
        if (temMais)
        {
            itens.RemoveAt(itens.Count - 1);
        }

        return new RecuperacoesSenhaPendentesResponse
        {
            Items = itens,
            PageSize = query.PageSize,
            ProximoCursor = temMais ? CursorNotificacoes.Criar(itens[^1].SolicitadaEmUtc, itens[^1].Id) : null,
        };
    }

    /// <summary>
    /// Emite a autorização depois da verificação humana declarada e da senha do responsável. Usa o pedido aberto do titular (ou cria um de
    /// origem atendimento) e substitui qualquer token anterior dele. O token só existe em claro nesta resposta.
    /// </summary>
    public async Task<AutorizacaoRecuperacaoSenhaResponse> EmitirAutorizacaoAsync(EmitirAutorizacaoRecuperacaoRequest request, CancellationToken ct)
    {
        contas.ExigirSqlServer();
        var contexto = request.Contexto;
        var agora = clock.GetUtcNow().UtcDateTime;
        var (responsavel, alvo) = await contas.AutorizarTerceiroAsync(contexto, request.UsuarioAfetadoId, agora, ct);
        contas.ConfirmarSenhaSolicitante(responsavel, request.SenhaAtualSolicitante!);

        var token = TokensRecuperacaoSenha.Gerar(out var hash);
        var expira = agora + TokensRecuperacaoSenha.Validade;
        var novaId = Guid.NewGuid();
        var recuperacaoId = await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async _ =>
        {
            await contas.TravarERevalidarAsync([responsavel, alvo], contexto, agora, ContasSenha.Conflito, ct);
            var aberta = await db.RecuperacoesSenha
                .FromSqlInterpolated($"SELECT * FROM [dbo].[Usuarios_recuperacoes_senha] WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE [UsuarioId] = {alvo.Id} AND [Status] IN (1, 2)")
                .SingleOrDefaultAsync(ct);
            if (aberta is not null && aberta.ExpiraEmUtc <= agora)
            {
                Encerrar(aberta, EncerramentoExpirada, agora);
                await db.SaveChangesAsync(ct);
                aberta = null;
            }

            if (aberta is null)
            {
                aberta = new RecuperacaoSenha
                {
                    Id = novaId,
                    UsuarioId = alvo.Id,
                    Origem = OrigemRecuperacaoSenha.Atendimento,
                    SolicitadaEmUtc = agora,
                    UltimaSolicitacaoEmUtc = agora,
                    Solicitacoes = 1,
                    ExpiraEmUtc = expira,
                };
                db.RecuperacoesSenha.Add(aberta);
            }

            // Repetição da mesma emissão (commit incerto) regrava os mesmos valores: nunca cria uma segunda autorização.
            aberta.Status = StatusRecuperacaoSenha.Autorizada;
            aberta.UsuarioAutorizadorId = responsavel.Id;
            aberta.IpAutorizador = contexto.Ip;
            aberta.MetodoVerificacao = request.MetodoVerificacao;
            aberta.AutorizadaEmUtc = agora;
            aberta.TokenHash = hash;
            aberta.TokenExpiraEmUtc = expira;
            aberta.SecurityVersion = alvo.SecurityVersion;
            if (aberta.ExpiraEmUtc < expira)
            {
                aberta.ExpiraEmUtc = expira;
            }

            await db.SaveChangesAsync(ct);
            return aberta.Id;
        }, ct);

        logger.LogInformation("Autorização de recuperação {RecuperacaoId} emitida por {UsuarioResponsavelId} para {UsuarioId} ({MetodoVerificacao}).",
            recuperacaoId, responsavel.Id, alvo.Id, request.MetodoVerificacao);
        return new AutorizacaoRecuperacaoSenhaResponse { RecuperacaoId = recuperacaoId, UsuarioId = alvo.Id, Token = token, ExpiraEmUtc = expira };
    }

    private static void Encerrar(RecuperacaoSenha recuperacao, string motivo, DateTime agora)
    {
        recuperacao.Status = StatusRecuperacaoSenha.Invalidada;
        recuperacao.EncerradaEmUtc = agora;
        recuperacao.MotivoEncerramento = motivo;
    }

    internal static bool ViolouUnicidade(Exception? ex) => ex is SqlException { Number: SqlUnicidadeIndice or SqlUnicidadeConstraint };
}
