using System.Text.Json.Serialization;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Almirante.Api.Dtos;

public class CargoDto
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public bool Ativo { get; set; }
    public required string CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; }
    public DateTime? UltimaAtualizacao { get; set; }
    public required string Role { get; set; }

    /// <summary>Perfil administrativo de sistema: não é desativado e é o único que recebe as permissões reservadas.</summary>
    public bool SistemaProtegido { get; set; }

    /// <summary>Versão a devolver nas alterações administrativas do cargo, das concessões e das regras por alvo (409 se mudou).</summary>
    public long Versao { get; set; }
}

/// <summary>Item do catálogo técnico de permissões (GET /api/Permissoes).</summary>
public sealed class PermissaoDto
{
    public required string Chave { get; set; }
    public required string Modulo { get; set; }
    public required string Descricao { get; set; }
    public bool Escrita { get; set; }
    public bool Ativo { get; set; }
    public bool ReservadaSistema { get; set; }
    public bool ExigeRegraAlvo { get; set; }
}

/// <summary>Concessões vigentes de um cargo e a versão para substituí-las.</summary>
public sealed class PermissoesCargoDto
{
    public Guid CargoId { get; set; }
    public required string Role { get; set; }
    public bool SistemaProtegido { get; set; }
    public long Versao { get; set; }
    public required IReadOnlyList<string> Permissoes { get; set; }
}

public sealed class RegraAlvoDto
{
    public required string Permissao { get; set; }
    public Guid CargoAlvoId { get; set; }
    public required string CargoAlvoRole { get; set; }
    public required string CargoAlvoNome { get; set; }
}

/// <summary>Regras por alvo vigentes do cargo solicitante e a versão para substituí-las. Ausência de regra nega.</summary>
public sealed class RegrasAlvoCargoDto
{
    public Guid CargoId { get; set; }
    public required string Role { get; set; }
    public long Versao { get; set; }
    public required IReadOnlyList<RegraAlvoDto> Regras { get; set; }
}

public sealed class MinhasPermissoesCargoDto
{
    public Guid Id { get; set; }
    public required string Role { get; set; }
    public required string Nome { get; set; }
}

/// <summary>GET /api/Me/Permissoes: permissões efetivas do próprio usuário. Conveniência do frontend; quem decide é sempre o backend.</summary>
public sealed class MinhasPermissoesDto
{
    public required MinhasPermissoesCargoDto Cargo { get; set; }

    /// <summary>Todas as permissões efetivas (do cargo e da designação), em ordem.</summary>
    public required IReadOnlyList<string> Permissoes { get; set; }

    /// <summary>As que vêm da designação vigente de responsável pelo almoxarifado (também presentes em <see cref="Permissoes"/>).</summary>
    public required IReadOnlyList<string> PermissoesPorDesignacao { get; set; }

    /// <summary>Revisão global da autorização: muda a cada alteração de cargo, concessão ou regra (recarregue o menu quando mudar).</summary>
    public long VersaoAutorizacao { get; set; }
}

/// <summary>
/// Base das alterações administrativas de autorização: motivo (auditado) e a senha atual de quem está logado (reautenticação, nunca
/// registrada). Recusa propriedades fora do contrato e redige o ToString().
/// </summary>
public abstract class AlteracaoAutorizacaoRequest
{
    public string? Motivo { get; set; }
    public string? SenhaAtualSolicitante { get; set; }

    [JsonIgnore, ValidateNever] public ContextoSolicitanteSenha Contexto { get; set; } = null!;

    public sealed override string ToString() => $"{GetType().Name} {ConteudoSensivelAttribute.MarcadorOmitido}";
}

/// <summary>POST /api/Cargos: o cargo nasce ativo, sem permissões nem regras por alvo.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CriarCargoRequest : AlteracaoAutorizacaoRequest
{
    public string? Role { get; set; }
    public string? Nome { get; set; }
    public string? Descricao { get; set; }
}

/// <summary>PUT /api/Cargos/{id}: só nome e descrição; role e proteção de sistema não são editáveis.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarCargoRequest : AlteracaoAutorizacaoRequest
{
    public string? Nome { get; set; }
    public string? Descricao { get; set; }
    public long? Versao { get; set; }
}

/// <summary>POST /api/Cargos/{id}/Desativar e /Reativar.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SituacaoCargoRequest : AlteracaoAutorizacaoRequest
{
    public long? Versao { get; set; }
}

/// <summary>PUT /api/Cargos/{id}/Permissoes: o conjunto inteiro; ausentes são revogadas.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SubstituirPermissoesCargoRequest : AlteracaoAutorizacaoRequest
{
    public long? Versao { get; set; }
    public List<string>? Permissoes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegraAlvoRequest
{
    public string? Permissao { get; set; }
    public Guid? CargoAlvoId { get; set; }
}

/// <summary>PUT /api/Cargos/{id}/RegrasAlvo: o conjunto inteiro de regras do cargo solicitante; ausentes são revogadas.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SubstituirRegrasAlvoRequest : AlteracaoAutorizacaoRequest
{
    public long? Versao { get; set; }
    public List<RegraAlvoRequest>? Regras { get; set; }
}
