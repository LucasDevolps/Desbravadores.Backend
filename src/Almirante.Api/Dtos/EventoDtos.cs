using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using MediatR;

namespace Almirante.Api.Dtos;

public sealed record TransporteRequest
{
    /// <summary>Valor por membro. Obrigatório no corpo; ignorado (tratado como zero) quando <c>ehGratis</c> é true.</summary>
    public decimal? Valor { get; init; }

    /// <summary>Obrigatório. Se true, o transporte é normalizado para zero.</summary>
    public bool? EhGratis { get; init; }
}

public sealed record AlimentacaoRequest
{
    /// <summary>Obrigatório. Se true, cada membro cuida da própria alimentação e o componente é zero.</summary>
    public bool? Individual { get; init; }

    /// <summary>Valor por membro. Obrigatório no corpo; ignorado (tratado como zero) quando <c>individual</c> é true.</summary>
    public decimal? Valor { get; init; }
}

public sealed record EventoItemAdicionalRequest
{
    public string? Titulo { get; init; }
    public decimal? Valor { get; init; }
}

/// <summary>Campos editáveis de um cadastro de evento, compartilhados por POST e PUT (mesma validação e normalização).</summary>
public abstract class EventoConteudoRequest
{
    /// <summary>Título do evento (ex.: "Reunião de clube"), 1–150 caracteres após trim e espaços colapsados.</summary>
    public string? Titulo { get; set; }

    /// <summary>Descrição opcional, até 2000 caracteres após trim; aceita quebras de linha. Vazia ou só espaços é gravada como nula.</summary>
    public string? Descricao { get; set; }

    /// <summary>Data do evento (yyyy-MM-dd). No POST, a partir do primeiro dia do mês atual (UTC).</summary>
    public DateOnly? DataEvento { get; set; }

    /// <summary>Local do evento, 1–200 caracteres após trim.</summary>
    public string? Local { get; set; }

    public TransporteRequest? Transporte { get; set; }
    public AlimentacaoRequest? Alimentacao { get; set; }

    /// <summary>Valor do seguro por membro; obrigatório, pode ser zero.</summary>
    public decimal? SeguroObrigatorio { get; set; }

    public IReadOnlyList<EventoItemAdicionalRequest>? ItensAdicionais { get; set; }

    /// <summary>IDs de Usuarios.Id: um GUID OU um array de GUIDs (normalizado para lista na desserialização).</summary>
    [JsonConverter(typeof(MembrosJsonConverter))]
    public IReadOnlyList<Guid>? Membros { get; set; }
}

/// <summary>POST /api/Eventos. Total, status, categoria, finalidade e responsável são definidos pelo servidor.</summary>
public sealed class RegistrarEventoRequest : EventoConteudoRequest, IRequest<RegistrarEventoResult>
{
    /// <summary>Cadastro de evento ativo já existente do MESMO passeio (mesma data e local), para outro grupo de valores.</summary>
    public Guid? EventoReferenciaId { get; set; }

    [JsonIgnore] public string? IdempotencyKey { get; set; }
    [JsonIgnore] public Guid UsuarioSolicitanteId { get; set; }
}

/// <summary>PUT /api/Eventos/{id}. Não altera id, agrupamento, referência, total, valor por membro nem status.</summary>
public sealed class UpdateEventoRequest : EventoConteudoRequest, IRequest<EventoDto>
{
    /// <summary>Versão (rowversion) devolvida pela API no cadastro; atualização concorrente resulta em 409.</summary>
    public string? Versao { get; set; }

    /// <summary>
    /// Obrigatório (1–255) quando a alteração remove participantes, zera o valor por membro de um evento com cobranças ou gera
    /// devoluções (redução do valor com lançamento Pago); alimenta a auditoria dos lançamentos desativados e fica gravado na devolução.
    /// </summary>
    public string? Motivo { get; set; }

    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

/// <summary>POST /api/Eventos/{id}/Participantes/{membroId}/Estorno: devolve ao membro tudo o que ele ainda tem retido no evento.</summary>
public sealed class EstornarParticipanteRequest : IRequest<EventoDto>
{
    /// <summary>Obrigatório, 1–255 caracteres; fica gravado na devolução.</summary>
    public string? Motivo { get; set; }

    /// <summary>Versão (rowversion) devolvida pela API no cadastro.</summary>
    public string? Versao { get; set; }

    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid MembroId { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
}

public sealed class DeleteEventoRequest : IRequest<Unit>
{
    /// <summary>Obrigatório, 1–255 caracteres.</summary>
    public string? Motivo { get; set; }

    /// <summary>Versão (rowversion) devolvida pela API no cadastro.</summary>
    public string? Versao { get; set; }

    [JsonIgnore] public Guid Id { get; set; }
    [JsonIgnore] public Guid UsuarioResponsavelId { get; set; }
    [JsonIgnore] public string IpResponsavel { get; set; } = string.Empty;
}

public sealed record ListEventosQuery(DateOnly? DataInicial, DateOnly? DataFinal) : IRequest<EventosResponse>;

public sealed record GetEventoQuery(Guid Id) : IRequest<EventoDto?>;

public sealed record RegistrarEventoResult(EventoDto Evento, bool Criado);

public sealed record TransporteDto(decimal Valor, bool EhGratis);

public sealed record AlimentacaoDto(bool Individual, decimal Valor);

public sealed record EventoItemAdicionalDto(string Titulo, decimal Valor);

public sealed record EventoLancamentoDto(Guid Id, Guid MembroId, decimal Valor, StatusLancamento Status, decimal? ValorPago = null,
    bool ValorAjustado = false);

public sealed record EventoIsencaoDto(Guid MembroId, Guid LancamentoId, string Motivo, Guid IsentoPorUsuarioId, DateTime IsentoEmUtc);

/// <summary>
/// Devolução de um pagamento do evento (issue #209): Saída para o membro com o valor esperado; valorDevolvido, dataDevolucao e
/// justificativa vêm da baixa feita pelo tesoureiro em /api/Lancamentos/{id}/Devolucao.
/// </summary>
public sealed record EventoDevolucaoDto(Guid Id, Guid MembroId, Guid LancamentoOrigemId, decimal Valor, DateOnly Vencimento,
    StatusLancamento Status, decimal? ValorDevolvido, DateOnly? DataDevolucao, string? Motivo, string? Justificativa);

public sealed record EventoDto
{
    public Guid Id { get; init; }
    public Guid EventoGrupoId { get; init; }
    public Guid? EventoReferenciaId { get; init; }
    public string Titulo { get; init; } = string.Empty;
    public string? Descricao { get; init; }
    public DateOnly DataEvento { get; init; }
    public required string Local { get; init; }
    public required TransporteDto Transporte { get; init; }
    public required AlimentacaoDto Alimentacao { get; init; }
    public decimal SeguroObrigatorio { get; init; }
    public IReadOnlyList<EventoItemAdicionalDto> ItensAdicionais { get; init; } = [];

    /// <summary>Sempre um array na resposta, mesmo que o POST/PUT tenha recebido um GUID único.</summary>
    public required IReadOnlyList<Guid> Membros { get; init; }
    public int QuantidadeMembros { get; init; }
    public decimal ValorPorMembro { get; init; }
    public decimal Total { get; init; }
    public decimal TotalEstimado { get; init; }
    public decimal TotalPago { get; init; }
    public decimal ValorAReceber { get; init; }
    public decimal TotalDevolvido { get; init; }
    public bool Ativo { get; init; }

    /// <summary>Token de concorrência (base64 do rowversion). Devolva-o em PUT/DELETE.</summary>
    public required string Versao { get; init; }

    /// <summary>
    /// Cobranças: uma por participante quando valorPorMembro &gt; 0; vazio quando o evento não tem valor a pagar. Um lançamento Pago
    /// continua aqui mesmo depois de o valor ser zerado ou de o participante ser removido (histórico do caixa).
    /// </summary>
    public required IReadOnlyList<EventoLancamentoDto> Lancamentos { get; init; }

    /// <summary>Devoluções geradas por redução do valor ou estorno de participante; vazio quando não há nenhuma.</summary>
    public IReadOnlyList<EventoDevolucaoDto> Devolucoes { get; init; } = [];

    public IReadOnlyList<EventoIsencaoDto> Isencoes { get; init; } = [];
}

public sealed record EventosResponse
{
    public required IReadOnlyList<EventoDto> Items { get; init; }
    public DateOnly DataInicial { get; init; }
    public DateOnly DataFinal { get; init; }

    /// <summary>Primeiro dia do mês corrente (UTC): menor data aceita no cadastro.</summary>
    public DateOnly DataMinimaCadastro { get; init; }
}
