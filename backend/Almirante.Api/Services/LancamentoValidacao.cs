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

    public static void ValidateValor(decimal valor)
    {
        if (valor < 0)
        {
            throw new LancamentoValidationException("Valor não pode ser negativo.");
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
}
