# ADR-0005: Separar identidades SQL da API e rotacionar a credencial de runtime

## Status

Accepted (registro retroativo em 2026-09-23). Introduzido e endurecido no PR #59 (issue #50).

## Contexto

Versões anteriores da API usavam `sa` ou logins de servidor com privilégio amplo. Há três registros
disso no repositório:

- a seção "MIGRAÇÃO" de `docs/sql/criar-usuario-admin-app.sql` converte ambientes que usavam `sa` ou
  os logins `almirante_admin_bd` e `almirante_user_bd`;
- o comentário de `DbAdminPrivilegeCheck` registra que o modo `Warn`/`Off` que existiu ali permitia
  rodar a API com `sa`;
- `SqlIdentityPolicy` recusa a configuração antiga `Security:AdminPrivilegeCheck` para que ela não
  volte a valer.

A API precisa de DDL para aplicar migrations
([ADR-0009](0009-aplicar-migrations-na-inicializacao-da-api.md)). Em operação normal, precisa apenas
ler e gravar dados de negócio. A auditoria não pode ser escrita diretamente pela aplicação
([ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md)).

## Decisão

A API usa duas identidades SQL dedicadas, ambas usuários contidos do banco (sem login de servidor).
Nenhuma delas é `sa`, e a senha da identidade de runtime é gerada e rotacionada pela própria API.

| Identidade | Quem a usa | Permissões |
| --- | --- | --- |
| `sa` | serviços `sqlserver` e `sql-bootstrap` do Compose e recurso SQL Server do AppHost | administrar a instância e provisionar as identidades abaixo. A API nunca a recebe. |
| Administrativa (`SQL_ADMIN_USER`, padrão `almirante_admin_bd`) | API, pela connection string `AlmiranteAdmin`, e a CLI `reset-admin-password` | `CREATE TABLE/VIEW/PROCEDURE/FUNCTION`, `CONTROL ON SCHEMA::dbo` e `ALTER ANY USER`, para migrations, concessões à role de runtime e troca da senha de runtime |
| Runtime (`SQL_APP_USER`, padrão `almirante_user_bd`) | `AlmiranteDbContext` em todas as requisições | membro apenas de `almirante_app_role`: `SELECT`/`INSERT`/`UPDATE` nas tabelas de negócio; `DELETE` só em `AuthSessions`; só `SELECT` em `__EFMigrationsHistory`, `lancamentos_deletados` e `historico_eventos`, com `DENY` de escrita nas duas últimas |

**Provisionamento (fora da API)**

`scripts/sql-bootstrap.sh` executa `docs/sql/criar-usuario-admin-app.sql` como `sa`, uma vez por
`docker compose up`, antes da API (`service_completed_successfully`). O script é idempotente e:

- habilita `contained database authentication` se preciso;
- cria o banco com `CONTAINMENT = PARTIAL` e `TRUSTWORTHY OFF`;
- cria as duas identidades e a role, normaliza papéis e permissões herdados;
- falha se a conferência final não bater.

As senhas chegam por variável de ambiente (`SQLCMDPASSWORD`, `APP_ADMIN_PASSWORD`), nunca por argumento
de linha de comando. O AppHost do Aspire executa o mesmo script num container `sql-bootstrap`.

**Startup da API** (`DbCredentialManager.InitializeAsync`, ativado quando `DbCredentials:AppUser` está
definido):

1. `SqlIdentityPolicy` recusa `SQL_SA_PASSWORD`/`MSSQL_SA_PASSWORD` no ambiente, `sa` em qualquer
   connection string e identidade administrativa ausente, sem credencial ou igual à de runtime. Não
   existe modo `Warn` nem `Off`.
2. `DbAdminPrivilegeCheck` confere os privilégios efetivos da identidade administrativa e recusa
   iniciar se ela for `sa` (inclusive pelo SID `0x01`), tiver papel ou permissão de servidor, papel
   fixo de banco, `IMPERSONATE`, for dona do banco ou tiver acesso a outro banco.
3. As migrations rodam com a identidade administrativa.
4. As permissões da role de runtime são recriadas tabela a tabela (`REVOKE` + `GRANT`/`DENY`).
5. A senha de runtime é trocada, e `DbPrivilegeAuditor.FindExcessPrivilegesAsync` confere o menor
   privilégio já com a senha nova.

**Credencial de runtime**

- Senha de 48 caracteres gerada com `RandomNumberGenerator`. Ela fica só em memória
  (`AppDbCredentialProvider`) e é aplicada a cada abertura de conexão pelo
  `AppDbCredentialInterceptor`. A connection string `almirante` não contém usuário nem senha.
- A rotação acontece em todo startup e a cada `DbCredentials:RotationHours` (padrão 24, faixa 1–720),
  pelo `DbCredentialRotationService`.
- Durante a troca, novas aberturas de conexão esperam, e o pool da credencial anterior é esvaziado.
- Erros do SQL Server são reduzidos a número e estado, para que a senha não apareça em log nem em
  exceção.

**Credencial administrativa**

A rotação é operacional: trocar `SQL_ADMIN_PASSWORD` e executar o bootstrap de novo, que faz
`ALTER USER`.

**Sem `DbCredentials:AppUser`**

No deploy Windows/IIS, por exemplo, a API usa diretamente a identidade da connection string. `sa`
continua recusado, e o restante da auditoria de privilégios segue `Security:DbPrivilegeCheck`
(`Warn` por padrão, `Enforce` ou `Off`).

**Fora do escopo desta decisão**

- A identidade humana `adm-ti` é opcional e criada à mão por
  [`docs/sql/criar-usuario-adm-ti.sql`](../../sql/criar-usuario-adm-ti.sql). Ela só lê views do schema
  `ti` e não é usada pela API.

## Consequências

### Positivas

- Um comprometimento do processo da API não dá acesso a privilégios de servidor, DDL em operação
  normal, outros bancos da instância nem escrita na auditoria.
- A senha de runtime nunca existe em disco, `.env` ou configuração, e muda sozinha periodicamente.
- Usuários contidos permitem rotacionar a senha com `ALTER USER`, restrito ao banco. Com logins, a
  rotação exigiria `ALTER ANY LOGIN`, que também permitiria redefinir senhas de outras aplicações da
  instância.
- Configuração insegura faz a startup falhar antes de qualquer alteração no banco, com mensagem que
  cita só nomes de opções.

### Negativas / trade-offs

- Só pode existir uma instância da API por banco. Cada startup rotaciona a senha de runtime e invalida
  a das outras instâncias ([`docs/authentication-security.md`](../../authentication-security.md#implantação)).
- A API depende de um bootstrap prévio executado com `sa`. Sem ele, a startup falha com mensagem que
  aponta o script.
- O bootstrap altera uma opção de nível de servidor (`contained database authentication`).
- Converter um ambiente antigo coloca o banco em `SINGLE_USER` por um curto período e derruba conexões
  da API anterior.
- Uma abertura de conexão que estava no meio do handshake no instante do `ALTER USER` pode falhar uma
  vez com erro 18456, como registrado em `DbCredentialManager.RotateAsync`.
- A startup faz dezenas de consultas de permissão (`HAS_PERMS_BY_NAME`, `IS_MEMBER`) antes de atender
  tráfego.
- O deploy Windows/IIS não usa a rotação e, por padrão, só registra aviso sobre privilégio excessivo.

## Alternativas consideradas

- **API conectada como `sa` ou por logins de servidor** (estado anterior). Substituída neste
  modelo; o bootstrap converte ambientes antigos.
- **Logins de servidor com rotação via `ALTER LOGIN`:** descartada, segundo
  [`docs/authentication-security.md`](../../authentication-security.md#identidades-sql-da-api-nenhuma-delas-é-sa),
  porque exigiria `ALTER ANY LOGIN`.
- **Auditoria da identidade administrativa em modo `Warn`/`Off`:** existiu e foi removida. A
  configuração antiga agora derruba a startup.

## Evidências no repositório

- [`DbCredentialManager.cs`](../../../backend/Almirante.Api/Infrastructure/DbCredentialManager.cs):
  `InitializeAsync`, `RotateAsync`, `BuildProvisionSql`, `BuildRotateSql`, `GeneratePassword`.
- [`AppDbCredentialProvider.cs`](../../../backend/Almirante.Api/Infrastructure/AppDbCredentialProvider.cs):
  `AppDbCredentialProvider`, `AppDbCredentialInterceptor`.
- [`DbCredentialRotationService.cs`](../../../backend/Almirante.Api/Services/DbCredentialRotationService.cs)
  e [`DbCredentialOptions.cs`](../../../backend/Almirante.Api/Options/DbCredentialOptions.cs).
- [`SqlIdentityPolicy.cs`](../../../backend/Almirante.Api/Infrastructure/SqlIdentityPolicy.cs) e
  [`DbPrivilegeAuditor.cs`](../../../backend/Almirante.Api/Infrastructure/DbPrivilegeAuditor.cs)
  (`FindAdminExcessPrivilegesAsync`, `DbAdminPrivilegeCheck`, `DbPrivilegeCheck`).
- [`Program.cs`](../../../backend/Almirante.Api/Program.cs): registro condicional de
  `DbCredentialManager` e validação de startup.
- Bootstrap: [`scripts/sql-bootstrap.sh`](../../../scripts/sql-bootstrap.sh) e
  [`docs/sql/criar-usuario-admin-app.sql`](../../sql/criar-usuario-admin-app.sql).
- Configuração: [`compose.yaml`](../../../compose.yaml) (serviços `sqlserver`, `sql-bootstrap` e
  `api`), [`.env.example`](../../../.env.example) e
  [`AppHost.cs`](../../../backend/Almirante.AppHost/AppHost.cs).
- Testes: [`SqlIdentityModelTests.cs`](../../../backend/Almirante.Api.Tests/SqlIdentityModelTests.cs),
  [`ApiProcessTests.cs`](../../../backend/Almirante.Api.Tests/ApiProcessTests.cs),
  [`DbPrivilegeTests.cs`](../../../backend/Almirante.Api.Tests/DbPrivilegeTests.cs),
  [`SqlIdentityPolicyTests.cs`](../../../backend/Almirante.Api.Tests/SqlIdentityPolicyTests.cs),
  [`DbCredentialManagerTests.cs`](../../../backend/Almirante.Api.Tests/DbCredentialManagerTests.cs),
  [`AdmTiPrivilegeTests.cs`](../../../backend/Almirante.Api.Tests/AdmTiPrivilegeTests.cs),
  [`scripts/tests/sql-bootstrap.test.sh`](../../../scripts/tests/sql-bootstrap.test.sh) e
  [`scripts/tests/compose-sql-identities.test.sh`](../../../scripts/tests/compose-sql-identities.test.sh).
- Documentação:
  [`docs/authentication-security.md`](../../authentication-security.md#identidades-sql-da-api-nenhuma-delas-é-sa).
- Histórico: PR #59 (commits `ee067d5`, `cbc8843` e `7a6fb06`).

## Decisões relacionadas

- [ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md): `DENY` de escrita nas
  tabelas de auditoria.
- [ADR-0009](0009-aplicar-migrations-na-inicializacao-da-api.md): as migrations rodam com a identidade
  administrativa.
