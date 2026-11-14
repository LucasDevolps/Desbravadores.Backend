using MediatR;

namespace Almirante.Api.Dtos;

/// <summary>GET /api/Tesouraria/Resumo. Sem parâmetros: o mês atual é o do fuso do clube.</summary>
public sealed record GetTesourariaResumoQuery : IRequest<TesourariaResumoResponse>;

public sealed record TesourariaResumoResponse
{
    /// <summary>Seis competências em ordem cronológica, dos cinco meses anteriores ao mês atual; meses sem movimento vêm com zero.</summary>
    public required IReadOnlyList<TesourariaCompetenciaDto> ReceitasDespesas { get; init; }
}

/// <summary>
/// Pagamentos registrados com dataPagamento no mês (1 a 12) do ano: soma de valorPago dos lançamentos ativos por tipoFluxo,
/// a mesma regra do financeiro de GET /api/Dashboard.
/// </summary>
public sealed record TesourariaCompetenciaDto(int Ano, int Mes, decimal Entradas, decimal Saidas);
