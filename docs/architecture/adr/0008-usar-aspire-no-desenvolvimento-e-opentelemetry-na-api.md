# ADR-0008: Usar .NET Aspire para desenvolvimento local e ServiceDefaults com OpenTelemetry na API

## Status

Accepted (registro retroativo em 2026-09-23). Presente desde o PR #1 (commit `56c0121`).

## Contexto

O PR #1, que criou o backend, já incluía `Almirante.AppHost` (".NET Aspire, SQL Server with a
persistent volume") e `Almirante.ServiceDefaults`, ao lado de um `Dockerfile` e de um `compose.yaml`.
A motivação para escolher o Aspire não está registrada no repositório.

A API roda em três contextos: pelo AppHost no desenvolvimento, pelo Docker Compose e pelo IIS. Nos
três, ela precisa de health checks para quem a orquestra e de um ponto único de configuração de
telemetria.

## Decisão

**Almirante.AppHost é usado só para desenvolvimento local.** `dotnet run --project
backend/Almirante.AppHost`:

- inicia um container SQL Server (`sql`) com `ContainerLifetime.Persistent` e o volume
  `almirante-sqlserver-data`;
- inicia o container `sql-bootstrap`, que executa o mesmo `scripts/sql-bootstrap.sh` do Compose;
- inicia a API como projeto .NET local (`almirante-api`), depois que o bootstrap termina
  (`WaitForCompletion`), com as connection strings `almirante` e `AlmiranteAdmin` e
  `DbCredentials__AppUser`. A senha do `sa` não é repassada à API
  ([ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md)).

Nessa topologia não há nginx. O AppHost não participa de nenhum deploy: `backend-deploy.yml`
publica apenas `Almirante.Api`, e o `Dockerfile` copia somente `Almirante.Api` e
`Almirante.ServiceDefaults`.

**Almirante.ServiceDefaults é uma biblioteca referenciada pela API em todos os ambientes.**
`builder.AddServiceDefaults()` e `app.MapDefaultEndpoints()` configuram:

- OpenTelemetry:
  - logs com mensagem formatada e escopos;
  - métricas de ASP.NET Core, `HttpClient` e runtime;
  - traces da fonte da aplicação, de ASP.NET Core (sem `/health` e `/alive`) e de `HttpClient`;
- exportação OTLP somente quando `OTEL_EXPORTER_OTLP_ENDPOINT` estiver definido;
- health check `self` (tag `live`) e os endpoints `/health` (todos os checks) e `/alive` (só `live`),
  mapeados em qualquer ambiente;
- service discovery e o handler padrão de resiliência para `HttpClient`.

**A integração Aspire do EF Core também é usada fora do Aspire.** A API registra o
`AlmiranteDbContext` com `AddSqlServerDbContext("almirante")`, do pacote
`Aspire.Microsoft.EntityFrameworkCore.SqlServer`, em todos os ambientes. Segundo
`EventosSqlSupport.cs`, esse registro liga `EnableRetryOnFailure`. Com `DbCredentials:AppUser`
definido, o health check do componente é desligado e substituído por `DbConnectivityHealthCheck`
(`almirante-db`), que abre conexão com a credencial injetada.

**Destino da telemetria**

- Pelo AppHost: inferido, o Aspire define `OTEL_EXPORTER_OTLP_ENDPOINT` na API apontando para o seu
  dashboard. O repositório configura o endpoint OTLP do dashboard em
  `backend/Almirante.AppHost/Properties/launchSettings.json` (`ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`).
- No Compose e no IIS: nenhuma configuração versionada define `OTEL_EXPORTER_OTLP_ENDPOINT`. Os logs
  vão para a saída padrão do processo (`docker compose logs`, ou log stdout do ANCM no IIS, habilitado
  pelo workflow).

**Fora do escopo desta decisão**

- Nenhuma plataforma de observabilidade (coletor OTLP, Prometheus, Grafana, Jaeger, Application
  Insights ou similar) faz parte do repositório. Exportar telemetria é uma capacidade configurável,
  não um componente implantado.

## Consequências

### Positivas

- O desenvolvedor sobe SQL Server, bootstrap e API com um comando e vê logs, métricas e traces no
  dashboard do Aspire sem infraestrutura adicional.
- A instrumentação é a mesma em todos os ambientes. Ligar a exportação num deploy exige só configurar
  `OTEL_EXPORTER_OTLP_ENDPOINT`, sem mudar código.
- `/health` atende os healthchecks do Compose (API e nginx) e a espera do deploy Linux.

### Negativas / trade-offs

- Nos deploys versionados não há destino para métricas e traces. Inferido: sem exportador, eles são
  coletados no processo e descartados. Os logs só ficam na saída padrão.
- A topologia de desenvolvimento difere da publicada: sem nginx, sem rate limit de borda e com
  `TrustServerCertificate=True` nas connection strings do AppHost. Inferido do `launchSettings.json`
  da API: ela roda como `Development`. Problemas de proxy e TLS só aparecem no Compose ou no CI
  (`nginx.test.sh`).
- `/health` e `/alive` são mapeados sem condição de ambiente, e o nginx os repassa
  ([ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md)).
- Service discovery e resiliência de `HttpClient` estão configurados, mas a API não usa `HttpClient`
  hoje. Não têm efeito prático.
- A API depende de um pacote de integração Aspire em produção. A estratégia de retry que ele liga obriga
  que unidades de trabalho transacionais rodem dentro de `CreateExecutionStrategy().ExecuteAsync`, como
  fazem `EventosService` e `LancamentosService`.
- O AppHost usa o mesmo nome de volume do Compose (`almirante-sqlserver-data`). Na mesma máquina, as
  duas formas de execução compartilham os dados e a senha do `sa` com que o volume foi criado.

## Alternativas consideradas

Alternativas históricas não foram encontradas no repositório.

## Evidências no repositório

- [`AppHost.cs`](../../../backend/Almirante.AppHost/AppHost.cs),
  [`Almirante.AppHost.csproj`](../../../backend/Almirante.AppHost/Almirante.AppHost.csproj) e
  [`launchSettings.json`](../../../backend/Almirante.AppHost/Properties/launchSettings.json).
- [`Extensions.cs`](../../../backend/Almirante.ServiceDefaults/Extensions.cs): `AddServiceDefaults`,
  `ConfigureOpenTelemetry`, `AddOpenTelemetryExporters`, `MapDefaultEndpoints`.
- [`Almirante.ServiceDefaults.csproj`](../../../backend/Almirante.ServiceDefaults/Almirante.ServiceDefaults.csproj):
  pacotes OpenTelemetry, `Microsoft.Extensions.ServiceDiscovery` e `Microsoft.Extensions.Http.Resilience`.
- [`Program.cs`](../../../backend/Almirante.Api/Program.cs): `AddServiceDefaults`,
  `AddSqlServerDbContext`, `DbConnectivityHealthCheck`, `MapDefaultEndpoints`.
- [`DbConnectivityHealthCheck.cs`](../../../backend/Almirante.Api/Infrastructure/DbConnectivityHealthCheck.cs).
- [`EventosSqlSupport.cs`](../../../backend/Almirante.Api.Tests/EventosSqlSupport.cs): retry equivalente
  ao do Aspire nos testes.
- [`Dockerfile`](../../../backend/Almirante.Api/Dockerfile), [`compose.yaml`](../../../compose.yaml)
  (healthchecks) e [`backend-deploy.yml`](../../../.github/workflows/backend-deploy.yml).
- Documentação: [`README.md`](../../../README.md#executar-com-net-aspire) e
  [`README.md`](../../../README.md#observabilidade-e-saúde).
- Histórico: PR #1 (commit `56c0121`).

## Questões em aberto

- A motivação para adotar o Aspire não está registrada.
- Não há registro de destino planejado para a telemetria dos ambientes publicados.

## Decisões relacionadas

- [ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md): o nginx só existe nos deploys.
- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): o AppHost reproduz o bootstrap
  e as identidades do Compose.
