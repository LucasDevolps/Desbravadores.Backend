using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Dtos;

public class LancamentoDto
{
    public Guid Id { get; set; }
    public Guid? MembroId { get; set; }
    public required string MembroNome { get; set; }
    public required string Tipo { get; set; }
    public string? Descricao { get; set; }
    public required string Categoria { get; set; }
    public decimal Valor { get; set; }
    public required string Moeda { get; set; }
    public required string Vencimento { get; set; }
    public required string Status { get; set; }
    public required string TipoFluxo { get; set; }
}

public class LancamentosResponse
{
    public required IReadOnlyList<LancamentoDto> Items { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class CreateLancamentoRequest
{
    public Guid? MembroId { get; set; }

    [Required]
    public required string MembroNome { get; set; }

    [Required]
    public required string Tipo { get; set; }

    public string? Descricao { get; set; }

    [Required]
    public required string Categoria { get; set; }

    [Required]
    public required string TipoFluxo { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Valor deve ser maior que zero.")]
    public decimal Valor { get; set; }

    public string? Moeda { get; set; }

    [Required]
    public required string Vencimento { get; set; }

    [Required]
    public required string Status { get; set; }
}

public class UpdateLancamentoRequest
{
    public Guid? MembroId { get; set; }
    public string? MembroNome { get; set; }
    public string? Tipo { get; set; }
    public string? Descricao { get; set; }
    public string? Categoria { get; set; }
    public string? TipoFluxo { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Valor deve ser maior que zero.")]
    public decimal? Valor { get; set; }

    public string? Moeda { get; set; }
    public string? Vencimento { get; set; }
    public string? Status { get; set; }
}

// ---- Lançamento geral (POST/GET/DELETE api/Lancamentos/Geral) ----

public class CreateLancamentoGeralRequest
{
    [Required]
    public required string Tipo { get; set; }

    [Required]
    public required string Categoria { get; set; }

    [Required]
    public required string TipoFluxo { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Valor deve ser maior que zero.")]
    public decimal Valor { get; set; }

    [Required]
    public required string Vencimento { get; set; }
}

public class LancamentoGeralResponse
{
    public Guid OperacaoId { get; set; }
    public int UsuariosProcessados { get; set; }
    public int LancamentosCriados { get; set; }
    public DateTime DataHoraUtc { get; set; }
}

// Resumo provisório: valores fixos, independentes do período ou dos lançamentos retornados.
// Ver LancamentosGeraisService.ResumoFixoProvisorio — deve ser substituído futuramente por um
// cálculo real (soma de entradas/despesas ativas no período).
public class ResumoFinanceiroDto
{
    public decimal TotalDespesas { get; set; }
    public decimal TotalEntradas { get; set; }
    public decimal SaldoAtual { get; set; }
}

public class LancamentosGeralListResponse
{
    public required IReadOnlyList<LancamentoDto> Lancamentos { get; set; }
    public required ResumoFinanceiroDto Resumo { get; set; }
}

public class DeleteLancamentoGeralRequest
{
    [Required]
    public required string Motivo { get; set; }
}

// ---- Registro flexível (POST api/Lancamentos/Registrar) ----

// Um único lançamento para um membro específico ou anônimo/despesa do clube (MembroId nulo), OU
// o mesmo lançamento para todos os usuários cadastrados (AplicarATodosOsMembros = true, mesmo
// mecanismo do lançamento geral — MembroId deve ficar nulo nesse caso). Ver
// LancamentosController.Registrar.
public class RegistrarLancamentoRequest
{
    public Guid? MembroId { get; set; }

    // Nome do membro (quando MembroId é informado, é sobrescrito pelo nome cadastrado) ou
    // descrição livre da origem/destino do lançamento quando anônimo/despesa do clube (ex.:
    // "Doação de empresário local", "Compra de material de escritório"). Obrigatório, exceto
    // quando AplicarATodosOsMembros = true (nesse caso, cada lançamento usa o nome do respectivo
    // usuário).
    public string? MembroNome { get; set; }

    [Required]
    public required string Tipo { get; set; }

    public string? Descricao { get; set; }

    [Required]
    public required string Categoria { get; set; }

    [Required]
    public required string TipoFluxo { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Valor deve ser maior que zero.")]
    public decimal Valor { get; set; }

    public string? Moeda { get; set; }

    [Required]
    public required string Vencimento { get; set; }

    [Required]
    public required string Status { get; set; }

    public bool AplicarATodosOsMembros { get; set; }
}
