namespace Almirante.Api.Entities;

// Idempotência do POST /api/Eventos (tabela eventos_operacoes), separada de LancamentosOperacoes (lançamento
// geral). A chave pertence ao par (UsuarioId, IdempotencyKey): outro usuário reutilizando o mesmo UUID
// não colide nem recebe a resposta alheia. A linha só existe se o evento, os participantes e os
// lançamentos foram persistidos na MESMA transação.
public sealed class EventoOperacao
{
    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public required string IdempotencyKey { get; set; }

    // SHA-256 (hex) da operação lógica normalizada (ver EventoHash): a forma JSON de "membros"
    // (GUID único x array), a ordem dos GUIDs e valores ignorados pelos booleanos não alteram o hash.
    public required string RequestHash { get; set; }

    public Guid EventoId { get; set; }

    // Resposta ORIGINAL do POST (EventoDto em JSON, inclusive a rowversion gerada pelo banco), gravada na mesma
    // transação do evento. É o que o replay devolve (200): nunca o estado atual do cadastro e nunca com a versão atual.
    // Nula só em operações anteriores a esta coluna (sem fonte confiável do resultado original): o replay delas
    // responde 409 com código estável (ver EventosService.ReplayAsync).
    public string? RespostaJson { get; set; }

    public DateTime CriadoEmUtc { get; set; }
}
