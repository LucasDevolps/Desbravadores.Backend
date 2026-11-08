# ADR-0016: Auditar chamadas HTTP com diário em disco e política de campos revisados

## Status

Accepted. Implementado na issue #158.

## Contexto

A issue #158 pede que toda chamada HTTP que chega à API, com sucesso ou erro, seja gravada no SQL Server
para análises futuras. Não pode haver amostragem, e o login fica de fora. O registro precisa ser
sanitizado antes de qualquer persistência: sem credenciais, CPF, e-mail, telefone, dados de
responsáveis, saúde, endereço ou arquivos. Ele também precisa sobreviver a rollback do negócio e a
indisponibilidade temporária do banco.

Restrições do repositório:

- o pipeline já tem `UseForwardedHeaders` restrito ao proxy confiável, exception handlers que consomem
  a exceção e escrevem Problem Details, rate limiting, JWT validado contra a sessão no banco e um hub
  SignalR que aceita `access_token` na query do handshake;
- a identidade SQL de runtime não tem `DELETE` e as tabelas de auditoria são protegidas por
  `DENY`/trigger ([ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md),
  [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md));
- as colunas são `varchar` (code page 1252) e não há broker de mensageria
  ([ADR-0012](0012-notificar-com-outbox-na-propria-linha-e-signalr.md) usa outbox no próprio SQL Server);
- a API roda no Docker Compose (volumes nomeados já usados para chaves e fotos) e no IIS.

## Decisão

- **Middleware próprio logo após `UseForwardedHeaders`**, antes de Swagger, exception handler, status
  pages, CORS, rate limiter, autenticação e endpoints. O registro é montado no `OnCompleted` (status,
  cabeçalhos e corpo finais, inclusive o Problem Details), uma única vez por chamada. O WebSocket é
  registrado no `101`. O corpo é copiado sem bufferizar: o da requisição enquanto a API o lê, o da
  resposta enquanto é escrito, com limite. Endpoint e valores de rota são lidos no início, porque o
  exception handler os apaga.
- **Política única de campos revisados** (`SanitizadorAuditoriaHttp`): lista explícita de nomes por
  categoria de valor, com credenciais e dados pessoais (com aliases e sem acento, caixa ou separador)
  vencendo a lista. Cada valor também precisa ter o formato da categoria. Texto livre vira tamanho. Só
  JSON é sanitizado; o resto fica com tipo e tamanho. A mesma política vale para URL, query, rota,
  cabeçalhos (lista permitida) e exceções (tipo, código e pilha, sem mensagem).
- **Diário em disco + gravador em segundo plano.** O registro sanitizado vira uma linha JSON anexada ao
  segmento ativo do processo, com flush para o sistema operacional. Um `BackgroundService` fecha o
  segmento a cada segundo, insere os registros em lotes idempotentes (pelo `Id`) com um `DbContext`
  próprio e só então apaga o arquivo. Um registro recusado de forma permanente vai para `rejeitados/`.
  Disco indisponível ou cheio: reserva em memória limitada e, depois dela, descarte contado, logado e
  sinalizado no health check (`Degraded`).
- **Tabela só de inserção** `auditoria_http`: trigger `INSTEAD OF UPDATE, DELETE`, `GRANT SELECT,
  INSERT` e `DENY UPDATE, DELETE` para a role de runtime, chave clusterizada por data e PK não
  clusterizada no `Id`.

## Consequências

### Positivas

- Cobre todo o pipeline, inclusive 404, 401, 403, 429 e erros, com a resposta que o cliente realmente
  recebeu.
- O banco fora do ar, um reinício ou a queda do processo não perdem o que já está no diário. As novas
  tentativas não duplicam registros, e o log não depende da transação do negócio.
- Um campo novo não vaza por padrão: só aparece depois de revisado. O conteúdo nunca sai da memória da
  requisição sem sanitização.
- Nenhuma infraestrutura nova: um volume no Compose e uma pasta no IIS.

### Negativas / trade-offs

- **Custo por requisição:** uma escrita sequencial em disco com flush, e a análise de JSON até 64 KB por
  direção.
- **Perdas possíveis:**
  - a reserva em memória se perde com o processo;
  - uma queda de energia pode levar as linhas ainda não sincronizadas (até um ciclo);
  - uma queda entre o fim da resposta e o `OnCompleted` perde aquela chamada.
- **A lista de campos revisados exige manutenção:** um campo novo aparece como omitido até ser incluído.
- **Volume:** a tabela cresce sem expurgo nesta entrega. O expurgo exigirá retirar o trigger em uma
  migration própria.
- **Respostas fora da API:** o que é produzido só pelo Nginx ou pelo IIS (rate limit do proxy, 413,
  502/504, TLS) continua só no access log do proxy.

## Alternativas consideradas

- **`HttpLogging` do ASP.NET Core:** grava no log, não no banco. Também não sanitiza o conteúdo do corpo
  campo a campo, e copiaria cabeçalhos e query por configuração genérica.
- **Fila só em memória (`Channel`) + `catch` e log:** perde tudo o que está na fila numa queda do
  processo e quando o banco fica fora por mais tempo que a capacidade dela. A issue a recusa como
  garantia.
- **INSERT síncrono na requisição:** acrescenta a latência do banco a toda chamada e falha junto com
  ele. Se usasse o `DbContext` da requisição, ficaria sujeito ao rollback do negócio.
- **Outbox numa tabela do próprio SQL Server:** não ajuda justamente quando o banco está indisponível.
- **Broker externo (RabbitMQ, Kafka):** é infraestrutura nova, que a issue pede para não introduzir sem
  necessidade.
- **Denylist de nomes ou regex sobre o corpo inteiro:** um campo novo ou um texto livre com dado pessoal
  passaria. A issue pede uma lista explícita.

## Evidências no repositório

- Código:
  - [`AuditoriaHttpMiddleware`](../../../src/Almirante.Api/Infrastructure/AuditoriaHttpMiddleware.cs)
  - [`CapturaCorpoHttp`](../../../src/Almirante.Api/Infrastructure/CapturaCorpoHttp.cs)
  - [`SanitizadorAuditoriaHttp`](../../../src/Almirante.Api/Security/SanitizadorAuditoriaHttp.cs)
  - [`PoliticaAuditoriaHttp`](../../../src/Almirante.Api/Security/PoliticaAuditoriaHttp.cs)
  - [`DiarioAuditoriaHttp`](../../../src/Almirante.Api/Services/DiarioAuditoriaHttp.cs)
  - [`GravadorAuditoriaHttp`](../../../src/Almirante.Api/Services/GravadorAuditoriaHttp.cs)
  - [`AlmiranteDbContext.AuditoriaHttp`](../../../src/Almirante.Api/Data/AlmiranteDbContext.AuditoriaHttp.cs)
  - migration `AddAuditoriaHttp`
- Testes:
  - [`AuditoriaHttpSanitizadorTests`](../../../src/Almirante.Api.Tests/AuditoriaHttpSanitizadorTests.cs)
  - [`AuditoriaHttpTests`](../../../src/Almirante.Api.Tests/AuditoriaHttpTests.cs)
  - [`AuditoriaHttpDiarioTests`](../../../src/Almirante.Api.Tests/AuditoriaHttpDiarioTests.cs)
  - [`AuditoriaHttpSqlServerTests`](../../../src/Almirante.Api.Tests/AuditoriaHttpSqlServerTests.cs)
- Documentação: [`docs/auditoria-http.md`](../../auditoria-http.md).

## Decisões relacionadas

- [ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md): proxy confiável e o que fica só
  no access log do Nginx.
- [ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md): auditorias de negócio,
  que esta complementa.
- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): permissões da role de runtime.
- [ADR-0008](0008-usar-aspire-no-desenvolvimento-e-opentelemetry-na-api.md): `TraceId` compartilhado
  com o OpenTelemetry.
