# ADR-0009: Aplicar as migrations do EF Core na inicialização da API

## Status

Accepted (registro retroativo em 2026-09-23). Decisão identificada durante o levantamento da
issue #69. Não estava na lista inicial da issue, mas está implementada e documentada no
`CONTRIBUTING.md` e no workflow de deploy.

## Contexto

O schema do banco evolui por migrations do EF Core em `backend/Almirante.Api/Data/Migrations`. Um push
em `develop` que altere o backend dispara o deploy automático (`backend-deploy.yml`), e nenhum passo
do workflow aplica migrations separadamente. O `CONTRIBUTING.md` registra: "Migrations são aplicadas
automaticamente na inicialização da API, inclusive no deploy".

## Decisão

A API aplica as migrations pendentes durante a própria inicialização, antes de atender requisições, e
em seguida executa um seed idempotente.

- **Com `DbCredentials:AppUser` definido** (Compose e AppHost), `DbCredentialManager.InitializeAsync`
  aplica as migrations com a identidade administrativa, depois da auditoria de privilégios e antes de
  conceder permissões e rotacionar a senha de runtime
  ([ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md)).
- **Sem `DbCredentials:AppUser`** (por exemplo, IIS), `DbSeeder.SeedAsync` aplica as migrations com a
  identidade da connection string.
- `DbSeeder.MigrateWithRetryAsync` repete a migração uma vez se o SQL Server responder o erro 1801
  (banco já existe), cenário que o código associa a reinícios de container.
- Depois das migrations, `DbSeeder.SeedAsync` cria, só se ainda não existirem:
  - os cargos;
  - o administrador inicial, com a senha validada pela política de senha;
  - um lançamento demonstrativo ("Mensalidade do clube"), quando a tabela `Lancamentos` está vazia.
- O deploy considera a publicação bem-sucedida quando `/health` responde:
  - no Linux, o workflow espera até 60 tentativas de 3 s;
  - no Windows, o job termina com um aviso para conferência manual, pois a subida pode levar até
    20 minutos.
- Regras para quem cria migrations, em `CONTRIBUTING.md`: migrations incrementais, sem reescrever as
  já aplicadas, preservando dados, com `Up` e `Down` avaliados e testes `RequiresSqlServer` quando
  houver trigger, índice, constraint, concorrência ou conversão de dados.

**Fora do escopo desta decisão**

- `AlmiranteDbContextFactory` existe só para o `dotnet ef` em tempo de design (gerar migrations) e não
  aceita `sa`.

## Consequências

### Positivas

- O deploy é um passo só: a versão da API que sobe é a que ajusta o schema de que precisa.
- Um banco novo fica utilizável sem passos manuais além do bootstrap SQL.
- A identidade de runtime nunca precisa de DDL. As migrations usam a identidade administrativa quando
  o modelo de identidades está ativo.
- Migration com erro impede a API de subir, e o healthcheck do deploy acusa a falha, em vez de a API
  atender com schema incompatível.

### Negativas / trade-offs

- A inicialização fica mais lenta à medida que migrations pesadas entram. O workflow de deploy já
  ampliou a espera por causa disso, segundo o comentário do passo "Aguardar API ficar saudável".
- Uma migration com falha derruba o serviço inteiro até ser corrigida, e o rollback do schema não é
  automático.
- Versões antiga e nova da API nem sempre podem coexistir sobre o mesmo schema. O README pede para não
  executá-las ao mesmo tempo depois de `ConvertLancamentoEnums`, e o `CONTRIBUTING.md` exige que cada
  PR descreva essa compatibilidade.
- Inferido: não existe coordenação entre instâncias que migram ao mesmo tempo. O modelo depende de uma
  única instância por banco, a mesma premissa exigida pela rotação de credencial.
- O seed roda em todos os ambientes. Um banco publicado sem lançamentos recebe o lançamento
  demonstrativo.

## Alternativas consideradas

Alternativas históricas não foram encontradas no repositório.

## Evidências no repositório

- [`Program.cs`](../../../backend/Almirante.Api/Program.cs): `DbCredentialManager.InitializeAsync` e
  `DbSeeder.SeedAsync` antes de `app.Run()`.
- [`DbSeeder.cs`](../../../backend/Almirante.Api/Data/DbSeeder.cs): `SeedAsync`,
  `MigrateWithRetryAsync`, `SeedCargosAsync`, `SeedAdminAsync`, `SeedLancamentosAsync`.
- [`DbCredentialManager.cs`](../../../backend/Almirante.Api/Infrastructure/DbCredentialManager.cs):
  `InitializeAsync`.
- [`AlmiranteDbContextFactory.cs`](../../../backend/Almirante.Api/Data/AlmiranteDbContextFactory.cs).
- Migrations: [`backend/Almirante.Api/Data/Migrations`](../../../backend/Almirante.Api/Data/Migrations).
- [`backend-deploy.yml`](../../../.github/workflows/backend-deploy.yml): passos "Aguardar API ficar
  saudável" e "Publicação concluída — verificar manualmente".
- Testes: [`MigrationsTests.cs`](../../../backend/Almirante.Api.Tests/MigrationsTests.cs)
  (`TodasAsMigrationsDoAssemblySaoDescobertasPeloEf`, `SnapshotCorrespondeAoModelo`),
  [`SqlServerIntegrationTests.cs`](../../../backend/Almirante.Api.Tests/SqlServerIntegrationTests.cs)
  (`Startup_AplicaTodasAsMigrations_ESchemaFunciona`),
  [`SqlIdentityModelTests.cs`](../../../backend/Almirante.Api.Tests/SqlIdentityModelTests.cs)
  (`Migrations_ExecutamComAdministradorDedicado_SemDbOwnerNemSysadmin`),
  [`EventosMigrationTests.cs`](../../../backend/Almirante.Api.Tests/EventosMigrationTests.cs) e
  [`LancamentosEnumMigrationTests.cs`](../../../backend/Almirante.Api.Tests/LancamentosEnumMigrationTests.cs).
- Documentação: [`CONTRIBUTING.md`](../../../CONTRIBUTING.md#mudanças-de-banco-e-migrations) e
  [`README.md`](../../../README.md#banco-de-dados-e-migrations).

## Decisões relacionadas

- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): identidade usada nas
  migrations e premissa de instância única.
- [ADR-0007](0007-testar-integracao-com-sql-server-real-no-ci.md): migrations aplicadas do zero em
  SQL Server real no CI.
