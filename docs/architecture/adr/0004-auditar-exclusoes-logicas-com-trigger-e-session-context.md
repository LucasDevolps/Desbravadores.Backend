# ADR-0004: Auditar exclusões lógicas com trigger no SQL Server e contexto via SESSION_CONTEXT

## Status

Accepted (registro retroativo em 2026-09-23). Introduzido no PR #39 (issue #14) para lançamentos e
estendido no PR #62 (issue #56) para eventos.

## Contexto

Lançamentos e eventos nunca são apagados fisicamente: a exclusão muda `Ativo` de `1` para `0`. A
issue #14 pede auditoria dessa exclusão lógica. O comentário da migration `AddLancamentoGeral` cita
os "requisitos 6/7" da issue. O registro precisa guardar quem excluiu, de qual IP, por qual motivo e
quando.

A API acessa o banco com uma identidade SQL técnica
([ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md)), então o banco não conhece o
usuário final. O comentário de `LancamentoDeletado` registra que o responsável é o usuário
autenticado, "nunca o usuário dono do lançamento (MembroId) nem o login da conexão SQL". Os dados de
auditoria também não podem vir do corpo da requisição.

## Decisão

A exclusão lógica é auditada por triggers do SQL Server. A API entrega o contexto da operação ao
banco pelo `SESSION_CONTEXT`, na mesma conexão e transação do `UPDATE`.

**No banco**

- `TR_Lancamentos_AuditoriaExclusaoLogica` (em `Lancamentos`) e `TR_eventos_AuditoriaExclusaoLogica`
  (em `eventos`) são triggers `AFTER UPDATE`. Eles só agem nas linhas que passam de `Ativo = 1` para
  `Ativo = 0`, inclusive em `UPDATE` de várias linhas.
- Os triggers leem `UsuarioResponsavelId`, `IpResponsavelExclusao` e `MotivoExclusao` do
  `SESSION_CONTEXT`. Se algum faltar, lançam erro (`THROW 50001` em lançamentos, `THROW 50011` em
  eventos) e o `UPDATE` é revertido.
- Cada exclusão grava um snapshot da linha anterior em `lancamentos_deletados` ou `historico_eventos`,
  com `ExcluidoEmUtc = SYSUTCDATETIME()` gerado pelo banco. O snapshot de eventos inclui participantes
  e lançamentos ativos em JSON.
- A identidade de runtime tem apenas `SELECT` nessas tabelas, com `DENY` de `INSERT`, `UPDATE` e
  `DELETE`. Os triggers gravam pela cadeia de propriedade de `dbo`.

**Na API**

- `SessaoAuditoria` define as três chaves com `sys.sp_set_session_context`. O responsável vem das
  claims validadas, o IP vem de `HttpContext.Connection.RemoteIpAddress` depois do middleware de
  forwarded headers ([ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md)), e o motivo
  vem do corpo da requisição, validado entre 1 e 255 caracteres.
- A conexão fica fixada (`OpenConnectionAsync`) durante a unidade de trabalho. O contexto é definido
  dentro da transação e limpo (valores `NULL`) no `finally`, na mesma conexão física.
- Se a limpeza falhar, `SqlConnection.ClearPool` marca a conexão para descarte antes de ela voltar ao
  pool. Assim, identidade e IP de uma operação não vazam para a próxima.
- Operações cobertas:
  - `DELETE /api/Lancamentos/{id}` (`LancamentoExclusaoService`);
  - `DELETE /api/Eventos/{id}`: o evento é desativado primeiro, depois participantes e lançamentos
    pendentes, para que o snapshot enxergue os participantes ativos;
  - `PUT /api/Eventos/{id}` que remove participantes: os lançamentos removidos são auditados pelo
    trigger de lançamentos.

**Fora do escopo desta decisão**

- Alterações (`PUT`) não geram histórico. `Lancamentos.AtualizadoPorUsuarioId` e
  `eventos.AtualizadoPorUsuarioId` guardam apenas o último responsável
  ([`docs/authentication-security.md`](../../authentication-security.md#matriz-de-acesso-31)).
- Com o provider InMemory, usado nos testes sem banco, `LancamentoExclusaoService` apenas desativa a
  linha, sem trigger. O fluxo de eventos recusa esse provider.

## Consequências

### Positivas

- Não existe exclusão lógica sem registro de auditoria. Mesmo um `UPDATE` direto pela identidade de
  runtime, sem contexto, falha no trigger.
- A aplicação não consegue criar, alterar nem apagar registros de auditoria. Isso é garantido por
  permissão no banco e testado.
- O horário da exclusão vem do relógio do banco, e o snapshot não depende de linhas que continuam
  mutáveis.
- Auditoria e exclusão ficam na mesma transação: ou as duas acontecem, ou nenhuma.

### Negativas / trade-offs

- A solução depende de recursos específicos do SQL Server (triggers, `SESSION_CONTEXT`, cadeia de
  propriedade). O provider InMemory não reproduz esse comportamento, e a validação exige SQL Server
  real ([ADR-0007](0007-testar-integracao-com-sql-server-real-no-ci.md)).
- O EF Core não pode usar `OUTPUT` sem `INTO` em tabelas com trigger. `Lancamentos` e `eventos` são
  mapeadas com `UseSqlOutputClause(false)`.
- Fixar a conexão, limpar o contexto e descartar o pool em caso de falha deixa o código de escrita mais
  complexo e sujeito a regressões sutis. Um teste dedicado cobre a limpeza.
- O texto completo dos triggers vive nas migrations. Mudar colunas auditadas exige recriar o trigger
  inteiro em uma nova migration, como já ocorreu em `AddTipoFluxo`, `UnifyLancamentosFlow` e
  `AddEventos`.
- Inferido: as chaves são definidas sem `@read_only = 1`, porque a mesma conexão precisa zerá-las no
  `finally`. Qualquer código que rode nessa conexão poderia sobrescrevê-las. Hoje só `SessaoAuditoria`
  as define.

## Alternativas consideradas

Alternativas históricas não foram encontradas no repositório.

## Evidências no repositório

- [`SessaoAuditoria.cs`](../../../backend/Almirante.Api/Infrastructure/SessaoAuditoria.cs):
  `DefinirContextoAuditoriaAsync`, `LimparAsync`.
- [`LancamentoExclusaoService.cs`](../../../backend/Almirante.Api/Services/LancamentoExclusaoService.cs):
  `DeleteAsync`.
- [`EventosService.cs`](../../../backend/Almirante.Api/Services/EventosService.cs): `ExecutarAsync`,
  `DeleteAsync`, `UpdateAsync`.
- Controllers [`LancamentosController.cs`](../../../backend/Almirante.Api/Controllers/LancamentosController.cs)
  e [`EventosController.cs`](../../../backend/Almirante.Api/Controllers/EventosController.cs): origem do
  responsável e do IP.
- Entidades [`LancamentoDeletado.cs`](../../../backend/Almirante.Api/Entities/LancamentoDeletado.cs) e
  [`HistoricoEvento.cs`](../../../backend/Almirante.Api/Entities/HistoricoEvento.cs).
- [`AlmiranteDbContext.cs`](../../../backend/Almirante.Api/Data/AlmiranteDbContext.cs):
  `UseSqlOutputClause(false)`.
- Triggers nas migrations
  [`20260916174441_AddLancamentoGeral.cs`](../../../backend/Almirante.Api/Data/Migrations/20260916174441_AddLancamentoGeral.cs),
  [`20260917120000_UnifyLancamentosFlow.cs`](../../../backend/Almirante.Api/Data/Migrations/20260917120000_UnifyLancamentosFlow.cs) e
  [`20260921160617_AddEventos.cs`](../../../backend/Almirante.Api/Data/Migrations/20260921160617_AddEventos.cs).
- Permissões: [`DbCredentialManager.cs`](../../../backend/Almirante.Api/Infrastructure/DbCredentialManager.cs)
  (`BuildProvisionSql`, `DENY` nas tabelas de auditoria) e
  [`DbPrivilegeAuditor.cs`](../../../backend/Almirante.Api/Infrastructure/DbPrivilegeAuditor.cs)
  (`AuditTables`).
- Testes: [`SqlServerIntegrationTests.cs`](../../../backend/Almirante.Api.Tests/SqlServerIntegrationTests.cs)
  (`ExclusaoLogica_RegistraResponsavelAutenticado_NaAuditoriaPorTrigger`),
  [`DbPrivilegeTests.cs`](../../../backend/Almirante.Api.Tests/DbPrivilegeTests.cs)
  (`Auditoria_AplicacaoNaoFalsificaNemAlteraHistorico_MasTriggerContinuaGravando`),
  [`EventosAuditIdentityTests.cs`](../../../backend/Almirante.Api.Tests/EventosAuditIdentityTests.cs),
  [`SessaoAuditoriaPoolSqlTests.cs`](../../../backend/Almirante.Api.Tests/SessaoAuditoriaPoolSqlTests.cs)
  (`Limpeza_DescartaConexaoComFalhaAntesDeFechar_MasReutilizaConexaoSaudavel`) e
  [`EventosSqlServerTests.cs`](../../../backend/Almirante.Api.Tests/EventosSqlServerTests.cs)
  (`Transacao_FalhaNoPut_NaoDeixaAlteracaoParcial_NemContextoDeAuditoria`).
- Documentação: [`README.md`](../../../README.md#eventos) (parágrafo "Auditoria") e
  [`docs/authentication-security.md`](../../authentication-security.md#correções-da-validação-da-pr-59).
- Histórico: PR #39 (commit `636cfb2`) e PR #62 (commit `bab8c84`).

## Decisões relacionadas

- [ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md): origem confiável do IP.
- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): permissões que impedem a
  escrita direta na auditoria.
