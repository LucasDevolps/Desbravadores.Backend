namespace Almirante.Api.Entities;

/// <summary>Capacidade do catálogo técnico (issue #182), mantido por migration a partir de Security.CatalogoPermissoes.</summary>
public class Permissao
{
    public Guid Id { get; set; }
    public required string Chave { get; set; }
    public required string Modulo { get; set; }
    public required string Descricao { get; set; }
    public bool Escrita { get; set; }
    public bool Ativo { get; set; } = true;
    public bool ReservadaSistema { get; set; }
    public bool ExigeRegraAlvo { get; set; }
}

/// <summary>Concessão de uma permissão a um cargo. Revogar é lógico (Ativo = 0); o par CargoId/PermissaoId é único.</summary>
public class CargoPermissao
{
    public Guid Id { get; set; }
    public Guid CargoId { get; set; }
    public Cargo? Cargo { get; set; }
    public Guid PermissaoId { get; set; }
    public Permissao? Permissao { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime ConcedidaEmUtc { get; set; }

    /// <summary>Nulo nas concessões do bootstrap (migration ou seed).</summary>
    public Guid? ConcedidaPorUsuarioId { get; set; }
    public DateTime? RevogadaEmUtc { get; set; }
    public Guid? RevogadaPorUsuarioId { get; set; }
}

/// <summary>
/// Regra por alvo: quem tem o cargo solicitante pode executar a operação (uma permissão com ExigeRegraAlvo) sobre usuários do cargo alvo.
/// Ausência de regra nega; não há curinga nem hierarquia implícita. A tripla é única; revogar é lógico.
/// </summary>
public class CargoRegraAlvo
{
    public Guid Id { get; set; }
    public Guid CargoSolicitanteId { get; set; }
    public Cargo? CargoSolicitante { get; set; }
    public Guid PermissaoId { get; set; }
    public Permissao? Permissao { get; set; }
    public Guid CargoAlvoId { get; set; }
    public Cargo? CargoAlvo { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime ConcedidaEmUtc { get; set; }
    public Guid? ConcedidaPorUsuarioId { get; set; }
    public DateTime? RevogadaEmUtc { get; set; }
    public Guid? RevogadaPorUsuarioId { get; set; }
}

/// <summary>
/// Registro único com a revisão global da autorização. Toda mudança de cargo, concessão ou regra incrementa a revisão na mesma transação
/// (trava exclusiva); as operações sensíveis leem com trava compartilhada até o commit e assim nunca decidem com uma matriz obsoleta.
/// </summary>
public class AutorizacaoEstado
{
    public const int IdUnico = 1;

    public int Id { get; set; }
    public long Revisao { get; set; }
}

/// <summary>Histórico imutável das alterações de autorização (só inserção; trigger recusa UPDATE e DELETE).</summary>
public class AutorizacaoHistorico
{
    public const string CargoCriado = "CargoCriado";
    public const string CargoAlterado = "CargoAlterado";
    public const string CargoDesativado = "CargoDesativado";
    public const string CargoReativado = "CargoReativado";
    public const string ConcessoesSubstituidas = "ConcessoesSubstituidas";
    public const string RegrasAlvoSubstituidas = "RegrasAlvoSubstituidas";

    public Guid Id { get; set; }
    public DateTime OcorridoEmUtc { get; set; }
    public Guid UsuarioResponsavelId { get; set; }
    public Guid CargoId { get; set; }
    public required string Operacao { get; set; }

    /// <summary>JSON do estado anterior (nulo na criação).</summary>
    public string? Antes { get; set; }

    /// <summary>JSON do estado posterior.</summary>
    public required string Depois { get; set; }
    public required string Motivo { get; set; }
    public required string IpResponsavel { get; set; }
    public required string TraceId { get; set; }
    public long RevisaoAutorizacao { get; set; }
}
