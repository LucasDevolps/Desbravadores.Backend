# ADR-0006: Controlar concorrência em eventos e sessões com rowversion

## Status

Accepted (registro retroativo em 2026-09-23). Em sessões, introduzido no PR #46. No contrato de
eventos, introduzido no PR #62 (issue #56).

## Contexto

Um cadastro de evento pode ser alterado por fluxos diferentes ao mesmo tempo:

- `PUT` e `DELETE` em `/api/Eventos/{id}`;
- `POST /api/Eventos` com `eventoReferenciaId`, que cria outro cadastro de preço no mesmo passeio;
- a mudança de status (pagamento) de um lançamento do evento por `PUT /api/Lancamentos/{id}`.

Sem controle, uma edição baseada em dados antigos poderia sobrescrever um pagamento ou uma alteração
de participantes feita por outra requisição. A descrição do PR #62 lista `rowversion` em `PUT` e
`DELETE`, a mudança de versão causada pelo status do lançamento e uma ordem única de locks.

Nas sessões de autenticação, duas renovações simultâneas do mesmo refresh token não podem gerar dois
sucessores válidos ([ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md)).

## Decisão

As tabelas em que escritas concorrentes precisam ser detectadas usam uma coluna `rowversion` do SQL
Server como token de concorrência otimista.

**Eventos (contrato público)**

- `eventos.Versao` é `rowversion` (`IsRowVersion`). Toda resposta de evento traz `versao`: os 8 bytes
  da `rowversion` em Base64.
- `PUT` e `DELETE` exigem `versao` no corpo. O validador recusa valores ausentes ou malformados com
  `400`.
- O primeiro passo de escrita no cadastro é um `UPDATE ... WHERE Id = @id AND Ativo = 1 AND
  Versao = @versao` (`TravarCadastroAsync`). Se nenhuma linha for afetada, a resposta é `404` (evento
  inexistente ou inativo) ou `409` (versão desatualizada). Um `DbUpdateConcurrencyException` no
  `SaveChanges` também vira `409`.
- Mudar o status de um lançamento de evento atualiza antes a linha do evento, o que muda a `versao`.
  Um `PUT` ou `DELETE` do evento feito com a versão anterior ao pagamento recebe `409`.
- Complemento: um lock de aplicação `sp_getapplock` exclusivo por `EventoGrupoId`, com dono na
  transação e espera máxima de 10 s (`EventosLockOptions`), serializa `POST` com referência, `PUT` e
  `DELETE` do mesmo passeio. Se a espera esgotar, a resposta é `409`. Todos os fluxos seguem a mesma
  ordem de locks: passeio, depois a linha do cadastro, depois participantes e lançamentos.

**Sessões (uso interno)**

- `AuthSessions.RowVersion` e `RefreshTokens.RowVersion` são `rowversion`. Se duas renovações
  concorrentes disputarem o mesmo refresh token, uma delas recebe `DbUpdateConcurrencyException` e a
  sessão é revogada (`concurrent-refresh`).

**Fora do escopo desta decisão**

- `Usuarios` usa `SecurityVersion` (`long`, `IsConcurrencyToken`), e não `rowversion`, para ordenar
  login e reset de senha ([ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md)).
- Lançamentos avulsos não têm token de concorrência. `PUT /api/Lancamentos/{id}` não recebe versão, e
  a última gravação prevalece.
- `Cargos` e `Usuarios` não têm endpoints de escrita.

## Consequências

### Positivas

- Atualizações perdidas em eventos são detectadas pelo banco, inclusive entre instâncias da API. O
  cliente recebe `409` explícito em vez de sobrescrever dados de outra operação.
- Um pagamento registrado depois da leitura do evento invalida edições feitas com a versão antiga, o
  que protege valores já cobrados.
- A ordem única de locks evita deadlock entre pagamento, `POST`, `PUT` e `DELETE` do mesmo passeio.
  Há teste de estresse para isso.
- Na autenticação, uma corrida entre renovações não produz dois refresh tokens válidos para a mesma
  sessão.

### Negativas / trade-offs

- O cliente precisa guardar a `versao` e consultar o evento de novo depois de um `409`. O replay
  idempotente do `POST` devolve a versão original, que pode estar desatualizada
  ([ADR-0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md)).
- Pagar qualquer lançamento de um evento invalida a versão que outro usuário tenha em mãos para editar
  o evento.
- Requisições que disputam o mesmo passeio podem esperar até 10 s pelo lock e, se esgotarem a espera,
  recebem `409 Passeio em uso`.
- O modelo não é uniforme: eventos usam `rowversion` no contrato, sessões o usam só internamente,
  usuários usam `SecurityVersion`, e lançamentos avulsos não têm controle otimista.
- Duas abas que renovam a mesma sessão ao mesmo tempo perdem a sessão. Isso protege contra uso
  paralelo de um token roubado, mas também afeta clientes legítimos que não coordenam a renovação.

## Alternativas consideradas

Alternativas históricas não foram encontradas no repositório.

## Evidências no repositório

- [`Evento.cs`](../../../backend/Almirante.Api/Entities/Evento.cs) (`Versao`),
  [`AuthSession.cs`](../../../backend/Almirante.Api/Entities/AuthSession.cs) e
  [`RefreshToken.cs`](../../../backend/Almirante.Api/Entities/RefreshToken.cs) (`RowVersion`).
- [`AlmiranteDbContext.cs`](../../../backend/Almirante.Api/Data/AlmiranteDbContext.cs): `IsRowVersion`
  e `SecurityVersion` com `IsConcurrencyToken`.
- [`EventosService.cs`](../../../backend/Almirante.Api/Services/EventosService.cs): comentário com a
  ordem de locks, `TravarGrupoAsync`, `TravarCadastroAsync`, `SalvarAsync`.
- [`LancamentosService.cs`](../../../backend/Almirante.Api/Services/LancamentosService.cs):
  `AtualizarStatusDeEventoAsync`.
- [`EventoRegras.cs`](../../../backend/Almirante.Api/Services/EventoRegras.cs):
  `VersaoParaTexto`, `TentarLerVersao`.
- [`AuthService.cs`](../../../backend/Almirante.Api/Services/AuthService.cs): tratamento de
  `DbUpdateConcurrencyException` em `RefreshAsync` e `LoginAsync`.
- Migrations [`20260921160617_AddEventos.cs`](../../../backend/Almirante.Api/Data/Migrations/20260921160617_AddEventos.cs)
  e [`20260917000000_AddAuthenticationSessions.cs`](../../../backend/Almirante.Api/Data/Migrations/20260917000000_AddAuthenticationSessions.cs).
- Testes: [`EventosSqlServerTests.cs`](../../../backend/Almirante.Api.Tests/EventosSqlServerTests.cs)
  (`Put_DoisPutsSimultaneosComAMesmaVersao_UmVenceEOutroRecebe409`,
  `MudancaDeStatusDoLancamento_AlteraAVersaoDoEvento_EInvalidaPutComVersaoAnterior`,
  `Delete_VersaoDesatualizada_Retorna409`),
  [`EventosGrupoLockSqlTests.cs`](../../../backend/Almirante.Api.Tests/EventosGrupoLockSqlTests.cs)
  (`PagamentoPutPostEDeleteSimultaneosNoMesmoPasseio_NaoGeramDeadlockNemEstadoIncoerente`),
  [`LancamentoPagamentoResilienciaSqlTests.cs`](../../../backend/Almirante.Api.Tests/LancamentoPagamentoResilienciaSqlTests.cs)
  e [`SecurityRegressionTests.cs`](../../../backend/Almirante.Api.Tests/SecurityRegressionTests.cs)
  (`ResetConcluidoNoMeioDoLogin_NaoDeixaSessaoUtilizavel`).
- Documentação: [`README.md`](../../../README.md#eventos) (parágrafo "Concorrência").
- Histórico: PR #46 (commit `3d4fe16`) e PR #62 (commit `bab8c84`).

## Questões em aberto

- Não foi encontrado teste automatizado para a revogação da sessão por renovação concorrente
  (`concurrent-refresh`).
- Não há registro no repositório sobre se a ausência de controle de concorrência em lançamentos
  avulsos é intencional.

## Decisões relacionadas

- [ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md): sessões e `SecurityVersion`.
- [ADR-0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md): replay com a versão original.
