using System.Globalization;
using Almirante.Api.Entities;

namespace Almirante.Api.Services;

// Validações compartilhadas entre o CRUD genérico de lançamentos (LancamentosService) e o
// lançamento geral (LancamentosGeraisService), para não duplicar as mesmas regras nos dois
// serviços.
public static class LancamentoValidacao
{
    public const string DateFormat = "yyyy-MM-dd";

    public static void ValidateTipo(string tipo)
    {
        if (!LancamentoTipos.Todos.Contains(tipo))
        {
            throw new LancamentoValidationException($"Tipo inválido: {tipo}.");
        }
    }

    public static void ValidateCategoria(string categoria)
    {
        if (!LancamentoCategorias.Todas.Contains(categoria))
        {
            throw new LancamentoValidationException($"Categoria inválida: {categoria}.");
        }
    }

    public static void ValidateStatus(string status)
    {
        if (!LancamentoStatuses.Todos.Contains(status))
        {
            throw new LancamentoValidationException($"Status inválido: {status}.");
        }
    }

    public static void ValidateTipoFluxo(string tipoFluxo)
    {
        if (!LancamentoTiposFluxo.Todos.Contains(tipoFluxo))
        {
            throw new LancamentoValidationException($"Tipo de fluxo inválido: {tipoFluxo}. Use \"Entrada\" ou \"Despesa\".");
        }
    }

    // Valor deve ser estritamente positivo: nem negativo, nem zero (um lançamento de R$ 0,00 não
    // representa nenhuma movimentação financeira real).
    public static void ValidateValor(decimal valor)
    {
        if (valor <= 0)
        {
            throw new LancamentoValidationException("Valor deve ser maior que zero.");
        }
    }

    public static DateOnly ParseVencimento(string vencimento)
    {
        if (!DateOnly.TryParseExact(vencimento, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new LancamentoValidationException("Vencimento deve usar o formato yyyy-MM-dd.");
        }

        return parsed;
    }

    // Vencimento não pode ser uma data já passada (comparação por dia, UTC do servidor — hoje é
    // permitido, só datas estritamente anteriores são rejeitadas). Chamado separadamente de
    // ParseVencimento porque UpdateLancamentoRequest só deve validar isso quando o vencimento está
    // de fato sendo alterado, não em toda atualização.
    public static void ValidateVencimentoNaoPassado(DateOnly vencimento)
    {
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        if (vencimento < hoje)
        {
            throw new LancamentoValidationException("Vencimento não pode ser uma data no passado.");
        }
    }
}
