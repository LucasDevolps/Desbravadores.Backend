using System.Text.Json.Serialization;
using Almirante.Api.Entities;
using Almirante.Api.Services;
using MediatR;

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
    public required TipoFluxoLancamento TipoFluxo { get; set; }
}

public class LancamentosResponse
{
    public required IReadOnlyList<LancamentoDto> Items { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

// Query de listagem (GET api/Lancamentos). Sem corpo de requisição, então não há um DTO
// pré-existente para reaproveitar como IRequest — os parâmetros vêm todos de query string.
public sealed record ListLancamentosQuery(
    int Page,
    int PageSize,
    string? Search,
    string? Status,
    string? Tipo,
    string? Data) : IRequest<LancamentosResponse>;

public class CreateLancamentoRequest : IRequest<LancamentoDto>
{
    public Guid? MembroId { get; set; }

    public required string MembroNome { get; set; }

    public required string Tipo { get; set; }

    public string? Descricao { get; set; }

    public required string Categoria { get; set; }

    public required TipoFluxoLancamento TipoFluxo { get; set; }

    public decimal Valor { get; set; }

    public string? Moeda { get; set; }

    public required string Vencimento { get; set; }

    public required string Status { get; set; }
}

public class UpdateLancamentoRequest : IRequest<LancamentoDto?>
{
    // Preenchido pelo controller a partir da rota (PUT /{id}), nunca vem do corpo da requisição.
    [JsonIgnore]
    public Guid Id { get; set; }

    public Guid? MembroId { get; set; }
    public string? MembroNome { get; set; }
    public string? Tipo { get; set; }
    public string? Descricao { get; set; }
    public string? Categoria { get; set; }
    public TipoFluxoLancamento? TipoFluxo { get; set; }

    public decimal? Valor { get; set; }

    public string? Moeda { get; set; }
    public string? Vencimento { get; set; }
    public string? Status { get; set; }
}

public sealed record DeleteLancamentoCommand(Guid Id) : IRequest<bool>;

// ---- Lançamento geral (POST/GET/DELETE api/Lancamentos/Geral) ----

public class CreateLancamentoGeralRequest : IRequest<LancamentoGeralCreateResult>
{
    // Preenchidos pelo controller a partir do header Idempotency-Key e das claims do usuário
    // autenticado, nunca vêm do corpo da requisição.
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [JsonIgnore]
    public Guid UsuarioSolicitanteId { get; set; }

    public required string Tipo { get; set; }

    public required string Categoria { get; set; }

    public required TipoFluxoLancamento TipoFluxo { get; set; }

    public decimal Valor { get; set; }

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

public sealed record ListLancamentosGeraisQuery(
    string? Periodo,
    string? DataInicio,
    string? DataFim) : IRequest<LancamentosGeralListResponse>;

public class DeleteLancamentoGeralRequest : IRequest<LancamentoGeralDeleteOutcome>
{
    // Preenchidos pelo controller a partir da rota, do usuário autenticado e do IP remoto, nunca
    // vêm do corpo da requisição.
    [JsonIgnore]
    public Guid Id { get; set; }

    [JsonIgnore]
    public Guid UsuarioResponsavelId { get; set; }

    [JsonIgnore]
    public string IpResponsavel { get; set; } = string.Empty;

    public required string Motivo { get; set; }
}

// ---- Registro flexível (POST api/Lancamentos/Registrar) ----

// Desfecho de RegistrarLancamentoRequest: ou um único lançamento foi criado (membro específico ou
// anônimo/despesa do clube), ou o mesmo lançamento foi aplicado a todos os membros, reaproveitando
// o desfecho do lançamento geral (LancamentoGeralCreateOutcome). Ver LancamentosController.Registrar.
public enum RegistrarLancamentoOutcome
{
    LancamentoUnicoCriado,
    GeralCriado,
    GeralReutilizado,
    GeralConflitoIdempotencia,
}

public sealed record RegistrarLancamentoResult(
    RegistrarLancamentoOutcome Outcome,
    LancamentoDto? Lancamento,
    LancamentoGeralResponse? Geral);

// Um único lançamento para um membro específico ou anônimo/despesa do clube (MembroId nulo), OU
// o mesmo lançamento para todos os usuários cadastrados (AplicarATodosOsMembros = true, mesmo
// mecanismo do lançamento geral — MembroId deve ficar nulo nesse caso). Ver
// LancamentosController.Registrar.
public class RegistrarLancamentoRequest : IRequest<RegistrarLancamentoResult>
{
    // Preenchidos pelo controller a partir do header Idempotency-Key e das claims do usuário
    // autenticado, nunca vêm do corpo da requisição.
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }

    [JsonIgnore]
    public Guid UsuarioSolicitanteId { get; set; }

    public Guid? MembroId { get; set; }

    // Nome do membro (quando MembroId é informado, é sobrescrito pelo nome cadastrado) ou
    // descrição livre da origem/destino do lançamento quando anônimo/despesa do clube (ex.:
    // "Doação de empresário local", "Compra de material de escritório"). Obrigatório, exceto
    // quando AplicarATodosOsMembros = true (nesse caso, cada lançamento usa o nome do respectivo
    // usuário).
    public string? MembroNome { get; set; }

    public required string Tipo { get; set; }

    public string? Descricao { get; set; }

    public required string Categoria { get; set; }

    public required TipoFluxoLancamento TipoFluxo { get; set; }

    public decimal Valor { get; set; }

    public string? Moeda { get; set; }

    public required string Vencimento { get; set; }

    public required string Status { get; set; }

    public bool AplicarATodosOsMembros { get; set; }
}
