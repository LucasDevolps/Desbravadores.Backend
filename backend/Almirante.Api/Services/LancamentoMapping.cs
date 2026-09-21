using System.Linq.Expressions;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;

namespace Almirante.Api.Services;

internal static class LancamentoMapping
{
    // Uma única projeção para consultas SQL e respostas após alterações.
    public static readonly Expression<Func<Lancamento, LancamentoDto>> Projection = lancamento => new LancamentoDto
    {
        Id = lancamento.Id,
        MembroId = lancamento.MembroId,
        MembroNome = lancamento.Membro != null ? lancamento.Membro.Nome : string.Empty,
        Finalidade = lancamento.Finalidade,
        Descricao = lancamento.Descricao,
        Categoria = lancamento.Categoria,
        TipoFluxo = lancamento.TipoFluxo,
        Valor = lancamento.Valor,
        Vencimento = lancamento.Vencimento,
        Status = lancamento.Status,
    };

    public static readonly Func<Lancamento, LancamentoDto> ToDto = Projection.Compile();

    public static Lancamento Criar(RegistrarLancamentoRequest request, Guid membroId, Guid? operacaoId, DateTime criadoEmUtc) => new()
    {
        Id = Guid.NewGuid(),
        MembroId = membroId,
        Finalidade = request.Finalidade,
        Descricao = request.Descricao,
        Categoria = request.Categoria,
        TipoFluxo = request.TipoFluxo,
        Valor = request.Valor,
        Vencimento = request.Vencimento,
        Status = StatusLancamento.Pendente,
        DataCriacao = criadoEmUtc,
        Ativo = true,
        OperacaoId = operacaoId,
    };
}
