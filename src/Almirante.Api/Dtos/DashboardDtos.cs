using MediatR;

namespace Almirante.Api.Dtos;

// GET /api/Dashboard. Datas em yyyy-MM-dd, informadas juntas; sem elas, o mês atual no fuso do clube. IncluirFinanceiro vem da
// permissão dashboard.financeiro.consultar do solicitante, nunca do cliente: sem ela o financeiro nem é consultado.
public sealed record GetDashboardQuery(string? DataInicial, string? DataFinal, bool IncluirFinanceiro) : IRequest<DashboardResponse>;

public sealed record DashboardResponse
{
    public required DashboardPeriodoDto Periodo { get; init; }

    // Null para quem não tem acesso financeiro (ex.: Secretário): os valores não são calculados nem enviados.
    public required DashboardFinanceiroDto? Financeiro { get; init; }
    public required DashboardDesbravadoresDto Desbravadores { get; init; }
    public required IReadOnlyList<DashboardEventoDto> ProximosEventos { get; init; }
    public required DashboardAtividadesPlanejadasDto AtividadesPlanejadas { get; init; }
    public required IReadOnlyList<DashboardEventoDto> AtividadesRecentes { get; init; }
}

// Período aplicado ao financeiro (limites inclusivos).
public sealed record DashboardPeriodoDto(DateOnly DataInicial, DateOnly DataFinal);

// Somente pagamentos registrados (dataPagamento no período, valorPago), de lançamentos ativos.
public sealed record DashboardFinanceiroDto(decimal Saldo, decimal Entradas, decimal Saidas);

// Total atual de usuários ativos com o cargo Desbravador; não depende do período.
public sealed record DashboardDesbravadoresDto(int QuantidadeAtivos);

// Passeios (EventoGrupoId) com cadastro ativo entre hoje e hoje + 29 dias, limites inclusivos, no fuso do clube. Sem limite de
// quantidade e sem depender do período financeiro.
public sealed record DashboardAtividadesPlanejadasDto(DateOnly DataInicial, DateOnly DataFinal, int Quantidade);

// Um passeio (EventoGrupoId), somando os participantes de todos os seus cadastros ativos. EventoIds são os cadastros ativos
// (GET /api/Eventos/{id}).
public sealed record DashboardEventoDto
{
    public Guid EventoGrupoId { get; init; }
    public required IReadOnlyList<Guid> EventoIds { get; init; }
    public DateOnly Data { get; init; }
    public required string Titulo { get; init; }
    public required string Local { get; init; }
    public int QuantidadeParticipantes { get; init; }
}
