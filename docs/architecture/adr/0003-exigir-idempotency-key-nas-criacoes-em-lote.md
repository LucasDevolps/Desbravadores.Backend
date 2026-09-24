# ADR-0003: Exigir Idempotency-Key persistida nas criações que geram lançamentos em lote

## Status

Accepted (registro retroativo em 2026-09-23). Introduzido no PR #39 (issue #14) para o lançamento
geral e estendido no PR #62 (issue #56) para eventos.

## Contexto

Duas operações criam vários registros financeiros de uma vez:

- `POST /api/Lancamentos/Registrar` com `aplicarATodosOsMembros: true` cria um lançamento para cada
  usuário cadastrado;
- `POST /api/Eventos` cria o cadastro do evento, os participantes e um lançamento pendente por
  membro.

Se o cliente reenviar uma dessas requisições depois de um timeout ou de uma falha de rede, a
cobrança seria duplicada. O PR #39 registra a idempotência persistida em `LancamentosOperacoes`,
com índice único e consistência sob concorrência real. O PR #62 define a idempotência de eventos
pelo par (usuário, `Idempotency-Key`), também com índice único, e grava tudo na mesma transação.

## Decisão

As duas operações exigem o header `Idempotency-Key`. A chave e um hash do conteúdo de negócio da
requisição são gravados no SQL Server, na mesma transação que cria os registros. Um índice único
garante que só uma requisição vence para cada chave, mesmo com requisições simultâneas em mais de uma
instância.

| Aspecto | Lançamento geral | Eventos |
| --- | --- | --- |
| Endpoint | `POST /api/Lancamentos/Registrar` com `aplicarATodosOsMembros: true` | `POST /api/Eventos` |
| Formato da chave | texto livre com espaços removidos nas pontas, até 100 caracteres | UUID, normalizado para o formato `D` |
| Tabela | `LancamentosOperacoes` | `eventos_operacoes` |
| Escopo da unicidade | índice único em `IdempotencyKey`: a chave é global | índice único em `(UsuarioId, IdempotencyKey)`: a chave é de cada usuário |
| Conteúdo do hash (SHA-256) | finalidade, descrição, categoria, fluxo, valor e vencimento; `Saida` entra como `"Despesa"` para preservar chaves antigas | prefixo `evento.v1`, data, local, booleanos e valores normalizados, `eventoReferenciaId` e membros ordenados |
| Mesma chave e mesmo hash | `200` com a resposta remontada da operação gravada (`OperacaoId`, contagens, data) | `200` com o DTO original gravado em `RespostaJson`, inclusive a `versao` original |
| Mesma chave e hash diferente | `409` | `409` |
| Corrida entre requisições | `DbUpdateException` no índice único, seguida de releitura da operação vencedora | violação de unicidade (erros 2601/2627), seguida de releitura e replay |
| Atomicidade | operação e lançamentos no mesmo `SaveChanges` | evento, participantes, lançamentos, operação e resposta original na mesma transação |
| Casos adicionais | não há | evento excluído depois: `409`; operação anterior à coluna `RespostaJson`: `409` com `codigo = EVENTO_IDEMPOTENCIA_SEM_RESPOSTA_ORIGINAL` |

Em eventos, o replay é resolvido antes da regra de data mínima do cadastro, para que um reenvio
legítimo continue válido depois da virada do mês.

**Fora do escopo desta decisão**

- `POST /api/Lancamentos/Registrar` para um único membro não é idempotente. O header só tem o
  tamanho validado.
- `PUT` e `DELETE` não usam chave de idempotência. Um segundo `DELETE` do mesmo recurso responde
  `404`, e o `PUT` de eventos depende da `versao`
  ([ADR-0006](0006-controlar-concorrencia-com-rowversion.md)).
- As chaves não expiram. Inferido: nenhum código remove linhas de `LancamentosOperacoes` ou
  `eventos_operacoes`, e a identidade de runtime não tem `DELETE` nessas tabelas
  ([ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md)).

## Consequências

### Positivas

- Um cliente pode repetir a mesma requisição com a mesma chave sem duplicar cobranças.
- A garantia vem do banco, e não da memória do processo. Ela vale sob concorrência e entre instâncias,
  e foi testada contra SQL Server real.
- Reusar uma chave com dados diferentes é recusado explicitamente com `409`, em vez de sobrescrever ou
  ignorar a operação anterior.
- Em eventos, uma chave usada por um usuário não expõe a resposta a outro usuário.

### Negativas / trade-offs

- Os dois endpoints têm contratos diferentes para a mesma ideia: texto livre e escopo global no
  lançamento geral, UUID e escopo por usuário em eventos. O cliente precisa conhecer as regras de cada
  um.
- Inferido do código: no lançamento geral o hash não inclui o solicitante. A mesma chave enviada por
  outro usuário com o mesmo conteúdo recebe a operação já existente, e com conteúdo diferente recebe
  `409`.
- As tabelas de operações crescem sem limite, porque as chaves não expiram.
- O formato do hash do lançamento geral ficou congelado (`"Despesa"`) para que chaves anteriores à
  migração de enums continuem válidas.
- O replay de eventos devolve o resultado original, não o estado atual. O cliente precisa consultar o
  `GET` antes de editar; um `PUT` com a `versao` original recebe `409` se o cadastro mudou.

## Alternativas consideradas

Alternativas históricas não foram encontradas no repositório. O PR #62 registra apenas que a
idempotência de eventos ficou separada de `LancamentosOperacoes`.

## Evidências no repositório

- [`LancamentosController.cs`](../../../backend/Almirante.Api/Controllers/LancamentosController.cs):
  `Registrar` e o header `Idempotency-Key`.
- [`LancamentoGeralService.cs`](../../../backend/Almirante.Api/Services/LancamentoGeralService.cs):
  `RegistrarAsync`, `CalcularHash`, `ResultadoExistente`.
- [`RegistrarLancamentoRequestValidator.cs`](../../../backend/Almirante.Api/Validation/Lancamentos/RegistrarLancamentoRequestValidator.cs):
  obrigatoriedade no modo geral e limite de 100 caracteres.
- [`LancamentoOperacao.cs`](../../../backend/Almirante.Api/Entities/LancamentoOperacao.cs) e
  [`EventoOperacao.cs`](../../../backend/Almirante.Api/Entities/EventoOperacao.cs).
- [`EventosService.cs`](../../../backend/Almirante.Api/Services/EventosService.cs): `RegistrarAsync`,
  `LerOperacaoAsync`, `ReplayAsync`, `SalvarAsync`.
- [`EventoRegras.cs`](../../../backend/Almirante.Api/Services/EventoRegras.cs):
  `NormalizarIdempotencyKey` e `Hash`.
- [`EventoValidators.cs`](../../../backend/Almirante.Api/Validation/Eventos/EventoValidators.cs): chave
  obrigatória em formato UUID.
- [`AlmiranteDbContext.cs`](../../../backend/Almirante.Api/Data/AlmiranteDbContext.cs): índices únicos
  de `LancamentoOperacao` e `EventoOperacao`.
- Migrations [`20260916174441_AddLancamentoGeral.cs`](../../../backend/Almirante.Api/Data/Migrations/20260916174441_AddLancamentoGeral.cs),
  [`20260921160617_AddEventos.cs`](../../../backend/Almirante.Api/Data/Migrations/20260921160617_AddEventos.cs) e
  [`20260921222826_AddEventosOperacoesResposta.cs`](../../../backend/Almirante.Api/Data/Migrations/20260921222826_AddEventosOperacoesResposta.cs).
- Testes: [`LancamentosConcurrencyTests.cs`](../../../backend/Almirante.Api.Tests/LancamentosConcurrencyTests.cs)
  (`RegistrarTodos_ConcorrenciaPersisteUmaOperacao_EUmLancamentoPorMembro`),
  [`EventosReplaySqlTests.cs`](../../../backend/Almirante.Api.Tests/EventosReplaySqlTests.cs)
  (`DuasRequisicoesEquivalentesConcorrentes_UmEventoUmaOperacaoEUmConjuntoDeLancamentos_RespostaOriginalConsistente`,
  `MesmaChaveParaUsuariosDistintos_IsolaOReplay_SemVazarRespostaAlheia`) e
  [`LancamentosTests.cs`](../../../backend/Almirante.Api.Tests/LancamentosTests.cs)
  (`RegistrarTodos_EIdempotente_EConflitaPayloadDiferente`).
- Documentação: [`README.md`](../../../README.md#eventos) (parágrafo "Idempotência").
- Histórico: PR #39 (commit `636cfb2`) e PR #62 (commit `bab8c84`).

## Decisões relacionadas

- [ADR-0006](0006-controlar-concorrencia-com-rowversion.md): a resposta original de eventos carrega a
  `versao` gerada no insert.
- [ADR-0007](0007-testar-integracao-com-sql-server-real-no-ci.md): a garantia sob concorrência é
  testada contra SQL Server real.
