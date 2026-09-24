# ADR-0007: Testar com SQL Server real, descartável, no CI

## Status

Accepted (registro retroativo em 2026-09-23). O job `sqlserver-integration` foi introduzido no
PR #59 (commit `6a25011`).

## Contexto

Parte importante do comportamento do sistema depende do SQL Server e não é reproduzida pelo provider
EF Core InMemory, usado nos testes sem banco:

- triggers de auditoria e `SESSION_CONTEXT`
  ([ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md));
- `rowversion`, `sp_getapplock` e ordem de locks
  ([ADR-0006](0006-controlar-concorrencia-com-rowversion.md));
- índices únicos (inclusive filtrados) sob concorrência real
  ([ADR-0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md));
- usuários contidos, permissões e rotação de senha
  ([ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md));
- migrations (`Up`/`Down`), constraints e o `UPDATE` atômico do lockout de login.

O repositório registra esse motivo. O commit `636cfb2` comentava no `.csproj` de testes que o SQL
Server real serve para validar "trigger, SESSION_CONTEXT, índice único", que o InMemory "não
reproduz fielmente". Hoje, `EventosService` recusa o provider InMemory com
`NotSupportedException("Eventos exigem SQL Server ...")`.

## Decisão

Testes que dependem do SQL Server rodam contra uma instância real, descartável, e são um check
obrigatório do CI.

**Marcação e harness**

- Esses testes têm `[Trait("Category", "RequiresSqlServer")]` e exigem a variável
  `ALMIRANTE_TEST_SQLSERVER`: uma conexão administrativa do harness, sem `Database`. Sem a variável
  eles falham de propósito, em vez de serem ignorados.
- `SqlServerApiFactory` cria um banco `almirante_test_<guid>` com `CONTAINMENT = PARTIAL` e um usuário
  contido próprio. Ela sobe a API pela `WebApplicationFactory`, que aplica todas as migrations no
  startup, e remove o banco ao final.
- `SqlIdentityEnvironment` prepara bancos e identidades com o mesmo script que o operador executa
  (`docs/sql/criar-usuario-admin-app.sql`, copiado para a saída dos testes pelo `.csproj`).
- `ApiProcess` executa o binário real da API (`Almirante.Api.dll`), configurado por variáveis de
  ambiente, como no Compose.
- A conexão do harness só cria e remove o ambiente. A API sob teste nunca a recebe.

**CI** ([`backend-ci.yml`](../../../.github/workflows/backend-ci.yml))

- O job `sqlserver-integration` roda em `ubuntu-latest`. Ele sobe `mcr.microsoft.com/mssql/server:2022-latest`
  com senha `sa` aleatória por execução (mascarada no log), porta publicada só em loopback e sem
  volumes, executa `--filter "Category=RequiresSqlServer"` e remove o container ao final.
- `build-and-test` roda em `windows-latest` e executa o restante da suíte com InMemory.
- Os dois jobs publicam cobertura para o job `coverage`.
- `build-and-test`, `sqlserver-integration` e `deploy-scripts` são checks obrigatórios nos rulesets
  de `main` e `develop` ([`README.md`](../../../README.md#proteção-de-branches)).
- Os jobs de CI usam só runners hospedados pelo GitHub. `scripts/tests/workflows.test.sh` impede que
  um workflow com gatilho `pull_request` use runner self-hosted.

## Consequências

### Positivas

- Comportamentos que a documentação de arquitetura descreve (auditoria, idempotência sob
  concorrência, concorrência otimista, menor privilégio) são verificados no mesmo motor usado em
  produção, a cada push e PR.
- As migrations são aplicadas do zero em cada banco de teste, o que detecta migrations quebradas antes
  do deploy ([ADR-0009](0009-aplicar-migrations-na-inicializacao-da-api.md)).
- O script de bootstrap versionado é o mesmo exercitado pelos testes.
- O CI não usa credenciais nem volumes de ambientes reais.

### Negativas / trade-offs

- O CI fica mais lento e depende de Docker no runner Linux. O job tem timeout de 25 minutos.
- Para rodar localmente, é preciso uma instância SQL Server com `sysadmin` para o harness. Sem
  `ALMIRANTE_TEST_SQLSERVER`, um `dotnet test` completo mostra cerca de 145 falhas
  ([`README.md`](../../../README.md#testes)).
- O harness cria bancos e logins e, no CI, usa `sa`. `CONTRIBUTING.md` proíbe apontá-lo para
  instância compartilhada ou de produção.
- A imagem `2022-latest` não é fixada por digest. Inferido: uma atualização da imagem pode mudar o
  comportamento dos testes entre execuções.
- O filtro de `build-and-test` ainda exclui `Category!=RequiresDocker`, mas nenhum teste usa mais essa
  categoria. É um resíduo da abordagem anterior.

## Alternativas consideradas

- **Testcontainers (`Testcontainers.MsSql`) com `Category=RequiresDocker`, excluídos do CI:** adotado
  no PR #39. Segundo o PR, o runner de CI da época (self-hosted Windows) não tinha Docker configurado,
  e esses testes rodavam só localmente. O pacote foi removido no commit `de2aa2d`. Depois, o harness
  passou a usar `ALMIRANTE_TEST_SQLSERVER`, e o PR #59 trouxe o job `sqlserver-integration` em runner
  hospedado.
- **Apenas o provider InMemory:** continua em uso para os testes sem banco, mas não cobre os
  comportamentos listados no Contexto.

## Evidências no repositório

- [`backend-ci.yml`](../../../.github/workflows/backend-ci.yml): jobs `build-and-test`,
  `sqlserver-integration` e `coverage`.
- [`Almirante.Api.Tests.csproj`](../../../backend/Almirante.Api.Tests/Almirante.Api.Tests.csproj):
  pacotes de teste e cópia de `docs/sql/*.sql` para a saída.
- Harness: [`SqlServerIntegrationTests.cs`](../../../backend/Almirante.Api.Tests/SqlServerIntegrationTests.cs)
  (`SqlServerApiFactory`), [`SqlIdentityEnvironment.cs`](../../../backend/Almirante.Api.Tests/SqlIdentityEnvironment.cs),
  [`ApiProcess.cs`](../../../backend/Almirante.Api.Tests/ApiProcess.cs) e
  [`AlmiranteApiFactory.cs`](../../../backend/Almirante.Api.Tests/AlmiranteApiFactory.cs) (InMemory).
- [`EventosService.cs`](../../../backend/Almirante.Api/Services/EventosService.cs): `ExecutarAsync`
  recusa provider não relacional.
- [`scripts/tests/workflows.test.sh`](../../../scripts/tests/workflows.test.sh): política de runners.
- Documentação: [`CONTRIBUTING.md`](../../../CONTRIBUTING.md#testes-e-ci),
  [`README.md`](../../../README.md#testes) e [`docs/test-coverage.md`](../../test-coverage.md).
- Histórico: PR #39 (commit `636cfb2`, Testcontainers), commit `de2aa2d` (remoção do pacote) e
  PR #59 (commit `6a25011`, job `sqlserver-integration`).

## Decisões relacionadas

- [ADR-0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md),
  [ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md),
  [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md) e
  [ADR-0006](0006-controlar-concorrencia-com-rowversion.md): comportamentos que só o SQL Server real
  valida.
