using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Os três caminhos que gravam uma senha nova pela API (issue #159): troca própria, troca administrativa e conclusão da recuperação assistida.
/// Todos terminam em <see cref="AplicarAsync"/>, na mesma transação: hash novo, SecurityVersion + 1, revogação de todas as sessões do
/// titular, encerramento das recuperações abertas, auditoria e, quando quem age não é o titular, a notificação com aviso pendente. Qualquer
/// falha desfaz tudo. Nenhum token é emitido: o titular precisa entrar de novo.
/// </summary>
public sealed class AlteracaoSenhaService(AlmiranteDbContext db, ContasSenha contas, TimeProvider clock, SinalAvisosNotificacoes sinal,
    ILogger<AlteracaoSenhaService> logger)
{
    public const string MotivoRevogacao = "alteracao-senha";
    public const string EncerramentoTokenConsumido = "token-consumido";
    public const string EncerramentoAlteracaoAdministrativa = "alteracao-administrativa";
    public const string EncerramentoSenhaAlterada = "senha-alterada";
    public const string EncerramentoAutorizadorSemPermissao = "autorizador-sem-permissao";

    public async Task AlterarPropriaAsync(AlterarPropriaSenhaRequest request, CancellationToken ct)
    {
        contas.ExigirSqlServer();
        var contexto = request.Contexto;
        var agora = clock.GetUtcNow().UtcDateTime;
        var titular = await contas.CarregarSolicitanteAsync(contexto, agora, ct);
        contas.ConfirmarSenhaSolicitante(titular, request.SenhaAtualSolicitante!);
        var novoHash = contas.ValidarECalcularNovoHash(titular, request.NovaSenha!);

        var alteracao = NovaAlteracao(titular.Id, titular.Id, TipoAlteracaoSenha.Propria, contexto.Ip, contexto.TraceId, agora);
        await GravarAsync(alteracao, titular, novoHash, async () =>
        {
            await contas.TravarERevalidarAsync([titular], contexto, agora, ContasSenha.Conflito, ct);
            return null;
        }, metodo: null, ct);
    }

    public async Task AlterarDeUsuarioAsync(AlterarSenhaUsuarioRequest request, CancellationToken ct)
    {
        contas.ExigirSqlServer();
        var contexto = request.Contexto;
        var agora = clock.GetUtcNow().UtcDateTime;
        var (responsavel, alvo) = await contas.AutorizarTerceiroAsync(contexto, request.UsuarioAfetadoId, agora, ct);
        contas.ConfirmarSenhaSolicitante(responsavel, request.SenhaAtualSolicitante!);
        var novoHash = contas.ValidarECalcularNovoHash(alvo, request.NovaSenha!);

        var alteracao = NovaAlteracao(responsavel.Id, alvo.Id, TipoAlteracaoSenha.Administrativa, contexto.Ip, contexto.TraceId, agora);
        await GravarAsync(alteracao, alvo, novoHash, async () =>
        {
            await contas.TravarERevalidarAsync([responsavel, alvo], contexto, agora, ContasSenha.Conflito, ct);
            return null;
        }, request.MetodoVerificacao, ct);
    }

    /// <summary>
    /// Conclusão sem login. Token inválido, expirado, usado, revogado ou obsoleto (senha trocada depois da emissão) recebem a mesma resposta;
    /// o autorizador e a matriz são conferidos de novo, porque o cargo dele pode ter mudado desde a emissão.
    /// </summary>
    public async Task ConcluirRecuperacaoAsync(ConfirmarRecuperacaoSenhaRequest request, CancellationToken ct)
    {
        contas.ExigirSqlServer();
        var agora = clock.GetUtcNow().UtcDateTime;
        if (!TokensRecuperacaoSenha.TentarCalcularHash(request.Token, out var hashToken))
        {
            throw TokenInvalido();
        }

        var recuperacao = await db.RecuperacoesSenha.AsNoTracking().SingleOrDefaultAsync(r => r.TokenHash == hashToken, ct);
        if (recuperacao is not { Status: StatusRecuperacaoSenha.Autorizada } || recuperacao.TokenExpiraEmUtc.GetValueOrDefault() <= agora)
        {
            throw TokenInvalido();
        }

        var alvo = await contas.CarregarAsync(recuperacao.UsuarioId, ct);
        if (alvo is not { Utilizavel: true } || alvo.SecurityVersion != recuperacao.SecurityVersion)
        {
            throw TokenInvalido();
        }

        var autorizador = await contas.CarregarAsync(recuperacao.UsuarioAutorizadorId!.Value, ct);
        if (autorizador is not { Utilizavel: true } || !PermissoesSenha.PodeAlterarSenhaDeTerceiro(autorizador.Role, alvo.Role))
        {
            await db.RecuperacoesSenha
                .Where(r => r.Id == recuperacao.Id && r.Status == StatusRecuperacaoSenha.Autorizada)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, StatusRecuperacaoSenha.Invalidada)
                    .SetProperty(r => r.EncerradaEmUtc, agora)
                    .SetProperty(r => r.MotivoEncerramento, EncerramentoAutorizadorSemPermissao), ct);
            logger.LogWarning("Recuperação {RecuperacaoId} invalidada: o autorizador não pode mais alterar a senha do titular.", recuperacao.Id);
            throw TokenInvalido();
        }

        var novoHash = contas.ValidarECalcularNovoHash(alvo, request.NovaSenha!);
        var alteracao = NovaAlteracao(autorizador.Id, alvo.Id, TipoAlteracaoSenha.RecuperacaoAssistida, request.Ip, request.TraceId, agora);
        alteracao.SolicitacaoRecuperacaoId = recuperacao.Id;
        await GravarAsync(alteracao, alvo, novoHash, async () =>
        {
            await contas.TravarERevalidarAsync([autorizador, alvo], contexto: null, agora, TokenInvalido, ct);
            var travada = await db.RecuperacoesSenha
                .FromSqlInterpolated($"SELECT * FROM [dbo].[Usuarios_recuperacoes_senha] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {recuperacao.Id}")
                .SingleOrDefaultAsync(ct);
            // Consumo atômico: outra conclusão paralela que chegou antes já mudou o status; esta encontra a linha encerrada e nada grava.
            return travada is { Status: StatusRecuperacaoSenha.Autorizada } && travada.TokenExpiraEmUtc.GetValueOrDefault() > agora
                && travada.SecurityVersion == alvo.SecurityVersion && travada.TokenHash.AsSpan().SequenceEqual(hashToken)
                ? travada
                : throw TokenInvalido();
        }, metodo: null, ct);
    }

    /// <summary>
    /// Transação única com o Id da auditoria gerado antes dela: se a execution strategy repetir depois de um commit cujo resultado se
    /// perdeu, a auditoria já existe e nada é refeito (sem segunda auditoria, notificação ou revogação). Qualquer outra repetição revalida
    /// tudo em <paramref name="travarERevalidar"/>, que devolve a recuperação consumida por esta operação (ou nula).
    /// </summary>
    private async Task GravarAsync(AlteracaoSenha alteracao, ContasSenha.Conta alvo, string novoHash,
        Func<Task<RecuperacaoSenha?>> travarERevalidar, MetodoVerificacaoIdentidade? metodo, CancellationToken ct)
    {
        var recuperacaoInformada = alteracao.SolicitacaoRecuperacaoId;
        await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async _ =>
        {
            if (await db.AlteracoesSenha.AsNoTracking().AnyAsync(a => a.Id == alteracao.Id, ct))
            {
                return true;
            }

            alteracao.SolicitacaoRecuperacaoId = recuperacaoInformada;
            var consumida = await travarERevalidar();
            await AplicarAsync(alteracao, alvo, novoHash, consumida, metodo, ct);
            return true;
        }, ct);

        if (NotificaTitular(alteracao))
        {
            sinal.Sinalizar();
        }

        // TODO(#159): aviso por e-mail desabilitado até existir provedor configurado e canal verificado do titular. Ao habilitar,
        // registrar uma implementação de IAvisoSenhaAlteradaEmail, injetá-la neste serviço e descomentar a chamada (sempre depois do
        // commit e sem desfazer a alteração se a entrega falhar; nada é enfileirado enquanto estiver desabilitado):
        // await avisoSenhaAlteradaEmail.EnviarAvisoSenhaAlteradaAsync(
        //     new AvisoSenhaAlterada(alteracao.UsuarioAfetadoId, alteracao.TipoAlteracao, alteracao.AlteradaEmUtc), ct);

        logger.LogInformation("Senha do usuário {UsuarioAfetadoId} alterada ({TipoAlteracao}) por {UsuarioResponsavelId}; auditoria {AlteracaoSenhaId}.",
            alteracao.UsuarioAfetadoId, alteracao.TipoAlteracao, alteracao.UsuarioResponsavelId, alteracao.Id);
    }

    private async Task AplicarAsync(AlteracaoSenha alteracao, ContasSenha.Conta alvo, string novoHash, RecuperacaoSenha? consumida,
        MetodoVerificacaoIdentidade? metodo, CancellationToken ct)
    {
        var agora = alteracao.AlteradaEmUtc;
        var atualizados = await db.Usuarios
            .Where(u => u.Id == alvo.Id && u.SecurityVersion == alvo.SecurityVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.SenhaHash, novoHash)
                .SetProperty(u => u.SecurityVersion, u => u.SecurityVersion + 1)
                .SetProperty(u => u.FalhasLoginConsecutivas, 0)
                .SetProperty(u => u.UltimaFalhaLoginUtc, (DateTime?)null)
                .SetProperty(u => u.LoginBloqueadoAteUtc, (DateTime?)null), ct);
        if (atualizados != 1)
        {
            throw ContasSenha.Conflito();
        }

        await db.AuthSessions
            .Where(s => s.UsuarioId == alvo.Id && s.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RevokedAtUtc, agora)
                .SetProperty(x => x.RevocationReason, MotivoRevogacao), ct);

        var abertas = consumida is not null
            ? [consumida]
            : await db.RecuperacoesSenha
                .FromSqlInterpolated($"SELECT * FROM [dbo].[Usuarios_recuperacoes_senha] WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE [UsuarioId] = {alvo.Id} AND [Status] IN (1, 2)")
                .ToListAsync(ct);
        foreach (var recuperacao in abertas)
        {
            recuperacao.EncerradaEmUtc = agora;
            switch (alteracao.TipoAlteracao)
            {
                case TipoAlteracaoSenha.RecuperacaoAssistida:
                    recuperacao.Status = StatusRecuperacaoSenha.Concluida;
                    recuperacao.MotivoEncerramento = EncerramentoTokenConsumido;
                    recuperacao.IpConclusao = alteracao.IpResponsavel;
                    break;
                case TipoAlteracaoSenha.Administrativa:
                    // O responsável atendeu o pedido trocando a senha diretamente: o pedido fica concluído e ligado à auditoria.
                    recuperacao.Status = StatusRecuperacaoSenha.Concluida;
                    recuperacao.MotivoEncerramento = EncerramentoAlteracaoAdministrativa;
                    if (recuperacao.UsuarioAutorizadorId is null)
                    {
                        recuperacao.UsuarioAutorizadorId = alteracao.UsuarioResponsavelId;
                        recuperacao.IpAutorizador = alteracao.IpResponsavel;
                        recuperacao.AutorizadaEmUtc = agora;
                        recuperacao.MetodoVerificacao = metodo;
                    }
                    alteracao.SolicitacaoRecuperacaoId = recuperacao.Id;
                    break;
                default:
                    recuperacao.Status = StatusRecuperacaoSenha.Invalidada;
                    recuperacao.MotivoEncerramento = EncerramentoSenhaAlterada;
                    break;
            }
        }

        db.AlteracoesSenha.Add(alteracao);
        if (NotificaTitular(alteracao))
        {
            db.Notificacoes.Add(NotificacoesFabrica.SenhaAlterada(alteracao.UsuarioAfetadoId, alteracao.Id, alteracao.TipoAlteracao, agora));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Notificação interna só quando outra pessoa agiu sobre a senha do titular: troca administrativa e recuperação assistida.</summary>
    private static bool NotificaTitular(AlteracaoSenha alteracao) =>
        alteracao.TipoAlteracao is TipoAlteracaoSenha.Administrativa or TipoAlteracaoSenha.RecuperacaoAssistida;

    private static AlteracaoSenha NovaAlteracao(Guid responsavelId, Guid afetadoId, TipoAlteracaoSenha tipo, string ip, string traceId,
        DateTime agora) => new()
    {
        Id = Guid.NewGuid(),
        UsuarioResponsavelId = responsavelId,
        UsuarioAfetadoId = afetadoId,
        IpResponsavel = ip,
        AlteradaEmUtc = agora,
        TipoAlteracao = tipo,
        TraceId = traceId,
    };

    public static ApiProblemException TokenInvalido() => new(StatusCodes.Status400BadRequest,
        "Autorização de recuperação inválida ou expirada.",
        "Nenhuma senha foi alterada. Procure um responsável autorizado do clube para receber uma nova autorização.");
}
