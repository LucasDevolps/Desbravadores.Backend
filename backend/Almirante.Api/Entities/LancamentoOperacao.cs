namespace Almirante.Api.Entities;

// Registro persistido de idempotência para o lançamento geral (POST /api/Lancamentos/Registrar no modo geral).
// A chave de idempotência (IdempotencyKey, vinda do header Idempotency-Key) é distinta do token
// JWT do solicitante. Uma linha aqui só existe depois que a operação (esta linha + todos os
// Lancamentos gerados) foi persistida com sucesso em um único SaveChanges — o índice único em
// IdempotencyKey garante, mesmo sob concorrência, que apenas uma requisição "vence" para cada
// chave (ver LancamentosService.RegistrarAsync).
public class LancamentoOperacao
{
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }

    // Hash (SHA-256, hex) dos dados de negócio da requisição (tipo, categoria, valor, vencimento).
    // Reenvio da mesma chave com o mesmo hash reaproveita o resultado já persistido; hash
    // diferente indica reuso indevido da chave e deve retornar 409.
    public required string RequestHash { get; set; }

    public required string Finalidade { get; set; }
    public string? Descricao { get; set; }
    public required string Categoria { get; set; }
    public required TipoFluxoLancamento TipoFluxo { get; set; }
    public decimal Valor { get; set; }
    public DateOnly Vencimento { get; set; }

    public int UsuariosProcessados { get; set; }
    public int LancamentosCriados { get; set; }

    public Guid CriadoPorUsuarioId { get; set; }
    public DateTime CriadoEmUtc { get; set; } = DateTime.UtcNow;
}
