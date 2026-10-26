using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Almirante.Api.Dtos;

/// <summary>Quem chama uma operação de senha autenticada: só das claims validadas e da conexão, nunca do corpo.</summary>
public sealed record ContextoSolicitanteSenha(Guid UsuarioId, Guid SessaoId, string Ip, string TraceId);

/// <summary>
/// Corpo das trocas autenticadas. Os DTOs de senha recusam qualquer propriedade fora do contrato (responsável, cargo, IP, data...) e
/// redigem o ToString(), para que nenhum log de argumentos exponha o conteúdo.
/// </summary>
public abstract class NovaSenhaConfirmadaRequest
{
    /// <summary>Senha atual de quem está autenticado. Na troca de outro usuário é a do responsável; a do afetado nunca é pedida.</summary>
    public string? SenhaAtualSolicitante { get; set; }

    public string? NovaSenha { get; set; }
    public string? ConfirmacaoNovaSenha { get; set; }

    [JsonIgnore, ValidateNever] public ContextoSolicitanteSenha Contexto { get; set; } = null!;

    public sealed override string ToString() => $"{GetType().Name} {ConteudoSensivelAttribute.MarcadorOmitido}";
}

/// <summary>POST /api/Auth/alterar-senha: o alvo é sempre o usuário do token.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AlterarPropriaSenhaRequest : NovaSenhaConfirmadaRequest, IRequest;

/// <summary>POST /api/Usuarios/{usuarioId}/alterar-senha: responsável autorizado pela matriz troca a senha de outro usuário.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AlterarSenhaUsuarioRequest : NovaSenhaConfirmadaRequest, IRequest
{
    /// <summary>Opcional: como a identidade foi confirmada, registrado no pedido de recuperação aberto que esta troca encerra.</summary>
    public MetodoVerificacaoIdentidade? MetodoVerificacao { get; set; }

    [JsonIgnore] public Guid UsuarioAfetadoId { get; set; }
}

/// <summary>POST /api/Auth/recuperacao-senha/solicitar: só o e-mail; nunca uma senha nova.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SolicitarRecuperacaoSenhaRequest : IRequest
{
    public string? Email { get; set; }

    [JsonIgnore, ValidateNever] public string Ip { get; set; } = string.Empty;

    public override string ToString() => $"{nameof(SolicitarRecuperacaoSenhaRequest)} {ConteudoSensivelAttribute.MarcadorOmitido}";
}

/// <summary>Resposta única do pedido público, para conta existente, inexistente, inativa ou pedido repetido.</summary>
public sealed class RecuperacaoSenhaSolicitadaResponse
{
    public const string MensagemPadrao =
        "Se for possível localizar sua conta, sua solicitação será tratada. Você também pode procurar um responsável autorizado do clube.";

    public string Mensagem { get; set; } = MensagemPadrao;
}

/// <summary>POST /api/Auth/recuperacao-senha/confirmar: token entregue pelo responsável, sem login nem senha antiga.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ConfirmarRecuperacaoSenhaRequest : IRequest
{
    public string? Token { get; set; }
    public string? NovaSenha { get; set; }
    public string? ConfirmacaoNovaSenha { get; set; }

    [JsonIgnore, ValidateNever] public string Ip { get; set; } = string.Empty;
    [JsonIgnore, ValidateNever] public string TraceId { get; set; } = string.Empty;

    public override string ToString() => $"{nameof(ConfirmarRecuperacaoSenhaRequest)} {ConteudoSensivelAttribute.MarcadorOmitido}";
}

/// <summary>POST /api/Usuarios/{usuarioId}/recuperacao-senha: emite a autorização de uso único depois da verificação humana.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EmitirAutorizacaoRecuperacaoRequest : IRequest<AutorizacaoRecuperacaoSenhaResponse>
{
    /// <summary>Senha do responsável autenticado (reautenticação); a do titular nunca é pedida.</summary>
    public string? SenhaAtualSolicitante { get; set; }

    public MetodoVerificacaoIdentidade? MetodoVerificacao { get; set; }

    [JsonIgnore] public Guid UsuarioAfetadoId { get; set; }
    [JsonIgnore, ValidateNever] public ContextoSolicitanteSenha Contexto { get; set; } = null!;

    public override string ToString() => $"{nameof(EmitirAutorizacaoRecuperacaoRequest)} {ConteudoSensivelAttribute.MarcadorOmitido}";
}

/// <summary>Única resposta que contém o token em claro: entregue ao titular pessoalmente e reservadamente; não é armazenado.</summary>
public sealed class AutorizacaoRecuperacaoSenhaResponse
{
    public Guid RecuperacaoId { get; set; }
    public Guid UsuarioId { get; set; }
    public required string Token { get; set; }
    public DateTime ExpiraEmUtc { get; set; }

    public override string ToString() => $"{nameof(AutorizacaoRecuperacaoSenhaResponse)} {ConteudoSensivelAttribute.MarcadorOmitido}";
}

public sealed record ListarRecuperacoesSenhaPendentesQuery(Guid SolicitanteId, int PageSize, string? Cursor)
    : IRequest<RecuperacoesSenhaPendentesResponse>;

/// <summary>Pedido aberto, sem token, IP ou dados de contato: o atendimento confere a identidade pessoalmente.</summary>
public sealed class RecuperacaoSenhaPendenteDto
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public required string Nome { get; set; }
    public required MeCargoDto Cargo { get; set; }
    public OrigemRecuperacaoSenha Origem { get; set; }
    public StatusRecuperacaoSenha Status { get; set; }
    public DateTime SolicitadaEmUtc { get; set; }
    public DateTime UltimaSolicitacaoEmUtc { get; set; }
    public int Solicitacoes { get; set; }
    public DateTime ExpiraEmUtc { get; set; }

    /// <summary>Validade da autorização emitida (nula sem autorização); vencida, basta emitir outra.</summary>
    public DateTime? AutorizacaoExpiraEmUtc { get; set; }
}

public sealed class RecuperacoesSenhaPendentesResponse
{
    public required IReadOnlyList<RecuperacaoSenhaPendenteDto> Items { get; set; }
    public int PageSize { get; set; }
    public string? ProximoCursor { get; set; }
}
