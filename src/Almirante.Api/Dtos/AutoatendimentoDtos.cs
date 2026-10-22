using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using MediatR;

namespace Almirante.Api.Dtos;

/// <summary>
/// PUT /api/Me: somente os dados pessoais do próprio usuário, com as mesmas regras e a mesma semântica de omissão do
/// PUT /api/Usuarios/{id}. Qualquer outra propriedade no corpo (nome, email, cargoId, senha, ativo, id...) é recusada com 400;
/// usuarioId e ipResponsavel nunca são lidos do corpo (vêm do token e da conexão).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AtualizarMeusDadosRequest : DadosPessoaisRequest, IRequest<MeusDadosDto>
{
    [JsonIgnore] public Guid UsuarioId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

/// <summary>GET /api/Me: dados do usuário do token.</summary>
public sealed record ObterMeusDadosQuery(Guid UsuarioId) : IRequest<MeusDadosDto>;

/// <summary>Dados do próprio usuário (GET/PUT /api/Me). Sem situação, auditoria, senha ou metadados do cargo.</summary>
public sealed class MeusDadosDto
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public string? Cpf { get; set; }
    public DateOnly? DataNascimento { get; set; }
    public string? Telefone { get; set; }
    public string? ResponsavelNome { get; set; }
    public string? ResponsavelEmail { get; set; }
    public string? ResponsavelCpf { get; set; }
    public string? ResponsavelTelefone { get; set; }
    public string? Rg { get; set; }
    public string? TipoSanguineo { get; set; }
    public string? DoencasAlergias { get; set; }
    public bool EhCin { get; set; }
    public DateOnly? DataExpiracao { get; set; }
    public EnderecoDto? Endereco { get; set; }

    /// <summary>Rota autenticada da própria foto (null sem foto).</summary>
    public string? FotoUrl => PossuiFoto ? "/api/Me/foto" : null;

    public required MeCargoDto Cargo { get; set; }

    [JsonIgnore] public bool PossuiFoto { get; set; }
}

/// <summary>GET /api/Me/Eventos: eventos ativos dos quais o usuário do token é participante ativo.</summary>
public sealed record ListarMeusEventosQuery(Guid UsuarioId) : IRequest<MeusEventosResponse>;

/// <summary>GET /api/Me/Eventos/{id}: null quando o evento não existe, foi excluído ou o usuário não participa dele.</summary>
public sealed record ObterMeuEventoQuery(Guid UsuarioId, Guid EventoId) : IRequest<EventoParticipanteDto?>;

public sealed record MeusEventosResponse
{
    /// <summary>Em ordem de data (passados e futuros), depois local e id.</summary>
    public required IReadOnlyList<EventoParticipanteDto> Items { get; init; }
}

/// <summary>
/// Evento visto pelo participante: data, local, composição do valor por membro e a própria cobrança. Não traz os outros
/// participantes, o total do evento, os lançamentos dos demais, a versão nem o agrupamento administrativo.
/// </summary>
public sealed record EventoParticipanteDto
{
    public Guid Id { get; init; }
    public DateOnly DataEvento { get; init; }
    public required string Local { get; init; }
    public required TransporteDto Transporte { get; init; }
    public required AlimentacaoDto Alimentacao { get; init; }
    public decimal SeguroObrigatorio { get; init; }
    public decimal ValorPorMembro { get; init; }
    public required MinhaCobrancaDto MinhaCobranca { get; init; }
}

/// <summary>Lançamento do próprio participante gerado pelo evento.</summary>
public sealed record MinhaCobrancaDto(decimal Valor, DateOnly Vencimento, StatusLancamento Status);

/// <summary>GET /api/Membros: usuários ativos para selecionar participantes e cobrados.</summary>
public sealed record ListarMembrosQuery : IRequest<IReadOnlyList<MembroResumoDto>>;

/// <summary>Identificação mínima de um membro: sem documentos, contato, saúde, responsável, endereço ou foto.</summary>
public sealed record MembroResumoDto(Guid Id, string Nome, string Cargo, string Role);
