# ADR-0012: Notificar com outbox na própria linha da notificação e avisar por SignalR

## Status

Accepted. Implementado na issue #130.

## Contexto

A issue #130 pede notificações por destinatário para cadastro de débito e de evento. Cada destinatário
tem a própria leitura, gravada no banco. Avisos devem chegar enquanto o usuário está conectado, sem
polling do frontend. A notificação só pode existir se o cadastro foi confirmado. Reenvios e
concorrência não podem duplicá-la. Uma falha temporária na entrega não pode perdê-la.

Restrições do repositório:

- a identidade SQL de runtime só tem `SELECT`, `INSERT` e `UPDATE` (`DELETE` apenas em `AuthSessions`;
  [`DbCredentialManager.BuildProvisionSql`](../../../src/Almirante.Api/Infrastructure/DbCredentialManager.cs),
  [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md));
- os cadastros já usam transação, execution strategy com retentativa e tratamento de commit incerto
  ([`SessaoAuditoria`](../../../src/Almirante.Api/Infrastructure/SessaoAuditoria.cs),
  [`EventosService`](../../../src/Almirante.Api/Services/EventosService.cs));
- a topologia tem uma instância da API por banco ([`compose.yaml`](../../../compose.yaml), rotação de
  senha em [`DbCredentialRotationService`](../../../src/Almirante.Api/Services/DbCredentialRotationService.cs)).

## Decisão

- A tabela `notificacoes` guarda uma linha por destinatário, com `LidaEmUtc` e a origem (`ChaveOrigem`,
  única por destinatário). Os serviços de lançamento e evento adicionam as notificações ao mesmo
  `DbContext` da operação, então elas são gravadas no mesmo `SaveChanges` ou na mesma transação.
- O aviso em tempo real pendente fica nas colunas `AvisoPendente`, `AvisoTentativas` e
  `AvisoProximaTentativaEmUtc` da própria notificação (outbox na linha). A leitura troca o aviso para
  `Lida` na mesma instrução que grava `LidaEmUtc`.
- O `PublicadorAvisosNotificacoes` (BackgroundService) reserva avisos confirmados com
  `UPDATE ... OUTPUT` e `READPAST`, emite pelo hub e limpa o aviso só se ele não mudou durante o envio.
  Em caso de falha, tenta de novo com atraso exponencial e um limite de tentativas. Um sinal em memória
  acorda o publicador logo após o commit. A varredura periódica garante a retomada.
- ASP.NET Core SignalR, hub `/hubs/notificacoes`, autenticado com o mesmo JWT e a mesma validação de
  sessão. O roteamento usa `Clients.User(Usuario.Id)`. O hub não tem métodos chamáveis. A conexão fecha
  quando o token expira (`CloseOnAuthenticationExpiration`). O `MonitorSessoesNotificacoes` revalida
  periodicamente as sessões das conexões abertas e encerra as revogadas.
- O token na query (`access_token`) só é aceito no `GET` do handshake do hub. O log de requisição do
  ASP.NET Core, o tracing do OpenTelemetry e o access log do Nginx não gravam essa query.

Fora do escopo: notificações de pagamento, alteração ou exclusão de evento, lembretes e comunicados.
Também ficam de fora um backplane para várias réplicas e o frontend.

## Consequências

### Positivas

- Notificação e cadastro são atômicos. Rollback não deixa notificação, e commit incerto não a duplica.
- Avisos sobrevivem a reinício e a falha de emissão, sem `DELETE` e sem tabela que cresce sem limpeza.
- A API REST continua a fonte da verdade. O hub é um atalho de latência.

### Negativas / trade-offs

- A entrega pelo hub é "pelo menos uma vez" e só alcança conexões da instância que emite. Várias
  réplicas exigem backplane (Redis ou Azure SignalR) ou sessões fixas.
- Colunas de transporte (aviso) convivem com o dado de domínio na mesma tabela.
- Há uma consulta periódica ao banco (padrão 5 s) mesmo sem avisos, servida por índice filtrado.
- Em ambientes com IIS, o log W3C precisa excluir a query para não gravar o token do handshake WebSocket.

## Alternativas consideradas

- Tabela de outbox separada: exigiria `DELETE` (privilégio que a identidade de runtime não tem) ou uma
  tabela que cresce sem limpeza.
- Emitir o aviso direto após o commit, sem persistir: um reinício ou uma falha perderia o aviso.
- Polling do frontend: descartado pela issue.
- Ticket de uso único para o handshake WebSocket: evitaria o JWT na query, mas exigiria estado
  compartilhado entre instâncias e um esquema de autenticação a mais. Preferimos aceitar o JWT só no
  handshake do hub e manter a query fora dos logs.

## Evidências no repositório

- [`NotificacoesFabrica`](../../../src/Almirante.Api/Services/NotificacoesFabrica.cs),
  [`NotificacoesService`](../../../src/Almirante.Api/Services/NotificacoesService.cs),
  [`PublicadorAvisosNotificacoes`](../../../src/Almirante.Api/Services/PublicadorAvisosNotificacoes.cs),
  [`MonitorSessoesNotificacoes`](../../../src/Almirante.Api/Services/MonitorSessoesNotificacoes.cs),
  [`NotificacoesHub`](../../../src/Almirante.Api/Hubs/NotificacoesHub.cs).
- [`Program.cs`](../../../src/Almirante.Api/Program.cs): `OnMessageReceived`, `MapHub`, `UseWebSockets`, filtro de log.
- Migration `20261021012556_AddNotificacoes`.
- Testes: [`NotificacoesTests.cs`](../../../src/Almirante.Api.Tests/NotificacoesTests.cs),
  [`NotificacoesSqlServerTests.cs`](../../../src/Almirante.Api.Tests/NotificacoesSqlServerTests.cs),
  [`NotificacoesMigrationTests.cs`](../../../src/Almirante.Api.Tests/NotificacoesMigrationTests.cs),
  [`nginx.test.sh`](../../../scripts/tests/nginx.test.sh).
- Contrato para o frontend: [`docs/notificacoes.md`](../../notificacoes.md).

## Decisões relacionadas

- [ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md): JWT e sessões persistidas.
- [ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md): Nginx como ponto de entrada.
- [ADR-0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md): Idempotency-Key.
- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): identidades SQL.
