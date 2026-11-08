namespace Almirante.Api.Entities;

public enum TipoAlteracaoSenha
{
    /// <summary>O titular, autenticado, trocou a própria senha confirmando a senha atual.</summary>
    Propria = 1,

    /// <summary>Um responsável autenticado (matriz de <see cref="Security.PermissoesSenha"/>) trocou a senha do titular com a própria senha.</summary>
    Administrativa = 2,

    /// <summary>O titular, sem sessão, concluiu com uma autorização de recuperação emitida por um responsável após verificação humana.</summary>
    RecuperacaoAssistida = 3,

    /// <summary>Reservado para a futura recuperação por canal comprovado (e-mail verificado), sem responsável: hoje desabilitada.</summary>
    RecuperacaoAutonoma = 4,
}

/// <summary>
/// Auditoria imutável de cada alteração de senha concluída (tabela Usuarios_alteracoes_senha): só inserção, na mesma transação da troca do
/// hash; um trigger recusa UPDATE e DELETE. Não guarda senha, hash, token nem cópia de dados pessoais. Tentativas recusadas não entram aqui.
/// </summary>
public sealed class AlteracaoSenha
{
    public Guid Id { get; set; }

    /// <summary>
    /// Quem executou (própria/administrativa) ou autorizou (recuperação assistida) a alteração. Nulo somente na recuperação autônoma, em que
    /// não há usuário autenticado (CHECK no banco); o CLI de emergência não grava aqui e nunca inventa um responsável.
    /// </summary>
    public Guid? UsuarioResponsavelId { get; set; }

    public Guid UsuarioAfetadoId { get; set; }

    /// <summary>
    /// IP observado de quem executou a requisição (após os proxies confiáveis). Na recuperação é o IP de quem concluiu com o token, que não
    /// estava autenticado; o IP e a data da autorização ficam na recuperação referenciada.
    /// </summary>
    public required string IpResponsavel { get; set; }

    public DateTime AlteradaEmUtc { get; set; }
    public TipoAlteracaoSenha TipoAlteracao { get; set; }

    /// <summary>Correlação com a requisição (trace id W3C ou identificador da requisição).</summary>
    public required string TraceId { get; set; }

    public Guid? SolicitacaoRecuperacaoId { get; set; }
}
