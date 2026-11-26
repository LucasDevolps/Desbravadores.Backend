using MediatR;

namespace Almirante.Api.Dtos;

public sealed record GetTesourariaResumoQuery : IRequest<TesourariaResumoResponse>;

public sealed record TesourariaResumoResponse
{
    public required decimal SaldoEmCaixa { get; init; }

    public required decimal ReceitaMes { get; init; }

    public required decimal DespesasMes { get; init; }

    public required IReadOnlyList<TesourariaCompetenciaDto> ReceitasDespesas { get; init; }
}

public sealed record TesourariaCompetenciaDto(int Ano, int Mes, decimal Entradas, decimal Saidas);
