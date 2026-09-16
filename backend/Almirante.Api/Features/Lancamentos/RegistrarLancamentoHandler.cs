using Almirante.Api.Dtos;
using Almirante.Api.Services;
using MediatR;

namespace Almirante.Api.Features.Lancamentos;

// Orquestra os dois modos de POST api/Lancamentos/Registrar: um único lançamento (membro
// específico ou anônimo/despesa do clube) via LancamentosService, ou o mesmo lançamento para todos
// os usuários cadastrados via LancamentosGeraisService (reaproveitando o mecanismo idempotente do
// lançamento geral). As regras de entrada (membroId x aplicarATodosOsMembros, membroNome
// obrigatório, idempotencyKey obrigatório) já foram checadas por RegistrarLancamentoRequestValidator
// antes deste Handler rodar.
public sealed class RegistrarLancamentoHandler(LancamentosService lancamentosService, LancamentosGeraisService lancamentosGeraisService)
    : IRequestHandler<RegistrarLancamentoRequest, RegistrarLancamentoResult>
{
    public async Task<RegistrarLancamentoResult> Handle(RegistrarLancamentoRequest request, CancellationToken cancellationToken)
    {
        if (request.AplicarATodosOsMembros)
        {
            return await RegistrarParaTodosOsMembrosAsync(request, cancellationToken);
        }

        var created = await lancamentosService.CreateAsync(new CreateLancamentoRequest
        {
            MembroId = request.MembroId,
            MembroNome = request.MembroNome!,
            Tipo = request.Tipo,
            Descricao = request.Descricao,
            Categoria = request.Categoria,
            TipoFluxo = request.TipoFluxo,
            Valor = request.Valor,
            Moeda = request.Moeda,
            Vencimento = request.Vencimento,
            Status = request.Status,
        }, cancellationToken);

        return new RegistrarLancamentoResult(RegistrarLancamentoOutcome.LancamentoUnicoCriado, created, null);
    }

    private async Task<RegistrarLancamentoResult> RegistrarParaTodosOsMembrosAsync(RegistrarLancamentoRequest request, CancellationToken cancellationToken)
    {
        var resultado = await lancamentosGeraisService.CreateAsync(
            request.IdempotencyKey!.Trim(),
            new CreateLancamentoGeralRequest
            {
                Tipo = request.Tipo,
                Categoria = request.Categoria,
                TipoFluxo = request.TipoFluxo,
                Valor = request.Valor,
                Vencimento = request.Vencimento,
            },
            request.UsuarioSolicitanteId,
            cancellationToken);

        return resultado.Outcome switch
        {
            LancamentoGeralCreateOutcome.Criado => new RegistrarLancamentoResult(RegistrarLancamentoOutcome.GeralCriado, null, resultado.Response),
            LancamentoGeralCreateOutcome.Reutilizado => new RegistrarLancamentoResult(RegistrarLancamentoOutcome.GeralReutilizado, null, resultado.Response),
            LancamentoGeralCreateOutcome.ConflitoIdempotencia => new RegistrarLancamentoResult(RegistrarLancamentoOutcome.GeralConflitoIdempotencia, null, null),
            _ => throw new InvalidOperationException($"Resultado inesperado: {resultado.Outcome}."),
        };
    }
}
