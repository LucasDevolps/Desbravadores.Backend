using MediatR;

namespace Almirante.Api.Dtos;

/// <summary>
/// GET /api/Dashboard. Datas em yyyy-MM-dd, informadas juntas; sem elas, o mês atual no fuso do clube. IncluirFinanceiro vem da
/// policy GestaoFinanceira do solicitante, nunca do cliente: sem ela o financeiro nem é consultado.
/// </summary>
public sealed record GetDashboardQuery(string? DataInicial, string? DataFinal, bool IncluirFinanceiro) : IRequest<DashboardResponse>;

public sealed record DashboardResponse
{
    public required DashboardPeriodoDto Periodo { get; init; }
    /// <summary>Null para quem não tem acesso financeiro (ex.: Secretário): os valores não são calculados nem enviados.</summary>
    public required DashboardFinanceiroDto? Financeiro { get; init; }
    public required DashboardDesbravadoresDto Desbravadores { get; init; }
    public required IReadOnlyList<DashboardEventoDto> ProximosEventos { get; init; }
}

/// <summary>Período aplicado ao financeiro (limites inclusivos).</summary>
public sealed record DashboardPeriodoDto(DateOnly DataInicial, DateOnly DataFinal);

/// <summary>Somente pagamentos registrados (dataPagamento no período, valorPago), de lançamentos ativos.</summary>
public sealed record DashboardFinanceiroDto(decimal Saldo, decimal Entradas, decimal Saidas);

/// <summary>Total atual de usuários ativos com o cargo Desbravador; não depende do período.</summary>
public sealed record DashboardDesbravadoresDto(int QuantidadeAtivos);

/// <summary>Um passeio (EventoGrupoId) com data a partir de hoje, somando os participantes de todos os seus cadastros ativos.</summary>
public sealed record DashboardEventoDto
{
    public Guid EventoGrupoId { get; init; }

    /// <summary>Cadastros ativos do passeio (GET /api/Eventos/{id}).</summary>
    public required IReadOnlyList<Guid> EventoIds { get; init; }
    public DateOnly Data { get; init; }
    public required string Local { get; init; }
    public int QuantidadeParticipantes { get; init; }
}
