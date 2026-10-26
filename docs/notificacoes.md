# Notificações (issue #130)

Notificações direcionadas a um usuário, com leitura individual gravada no banco e aviso em tempo real
por SignalR. Este documento descreve o comportamento do backend e o contrato que o frontend deve
seguir. O frontend ainda não foi implementado nem validado.

## Visão geral

- O banco é a fonte da verdade. A API REST lista as pendências e registra a leitura.
- O hub SignalR (`/hubs/notificacoes`) apenas **avisa** que algo mudou, para a tela atualizar sem
  recarregar. Um aviso pode chegar repetido ou não chegar (por exemplo, com o usuário desconectado):
  o cliente sempre se reconcilia com a API.
- Cada notificação pertence a um único destinatário. A identidade vem sempre do token validado; nenhum
  endpoint aceita `usuarioId` do cliente.
- Qualquer usuário autenticado com sessão válida usa o próprio módulo, inclusive desbravadores (`DS`).
  Os endpoints de gestão de eventos e lançamentos seguem as policies `GestaoEventos` e `GestaoFinanceira`
  (matriz em [`authentication-security.md`](authentication-security.md#perfis-e-matriz-de-acesso-31-127)).

## Quando uma notificação é criada

| Gatilho | Destinatários | Tipo | Referências |
|---|---|---|---|
| `POST /api/Lancamentos/Registrar` individual, `tipoFluxo: Entrada` | o `membroId` do lançamento | `DebitoCadastrado` | `lancamentoId` |
| `POST /api/Lancamentos/Registrar` com `aplicarATodosOsMembros: true`, `Entrada` | cada usuário que recebeu um lançamento na operação (a regra atual não mudou: todos os usuários cadastrados), cada um com a própria notificação | `DebitoCadastrado` | `lancamentoId` do lançamento daquele usuário |
| `POST /api/Eventos` (novo cadastro) | cada participante (`membros`) daquele cadastro, uma vez | `EventoCadastrado` | `eventoId` e `lancamentoId` |
| `POST /api/Usuarios/{id}/alterar-senha` e `POST /api/Auth/recuperacao-senha/confirmar` (issue #159) | só o titular da senha alterada, quando quem agiu foi outra pessoa (a troca própria não notifica) | `SenhaAlterada` | nenhuma (`ChaveOrigem` = `senha:{id da auditoria}`) — ver [`senhas.md`](senhas.md#notificação-interna) |

Regras:

- **Débito** é um lançamento de `Entrada` (o clube recebe) atribuído a um membro, como mensalidades e
  as cobranças de evento. `Saida` é despesa do clube (nome legado `Despesa`): mesmo com membro, não é
  cobrança e não gera notificação.
- O evento gera uma única notificação por participante, já com data, local, valor e vencimento do
  lançamento do evento. O lançamento interno do evento não gera uma segunda notificação de débito.
  Participantes de outros cadastros do mesmo passeio não são notificados.
- A notificação é gravada na **mesma transação** do cadastro. Se o cadastro falhar ou for desfeito,
  não existe notificação. Ela só fica visível e só é anunciada depois do commit.
- Reenvios idempotentes (mesma `Idempotency-Key`), concorrência e repetição de processamento não
  recriam notificações. O banco garante isso com o índice único `(DestinatarioId, ChaveOrigem)`.
- Não há notificações retroativas para registros anteriores à migration `AddNotificacoes`.
- Fora do escopo desta versão: pagamento, alteração ou exclusão de evento (inclusive membros adicionados
  por `PUT /api/Eventos/{id}`), lembretes e comunicados. O modelo (`Tipo`, `ChaveOrigem`, referências
  opcionais) já comporta novos tipos.

### Idempotência do débito individual

`Idempotency-Key` continua **opcional** no modo individual, e o comportamento sem ela não mudou:

- sem a chave, cada `POST` é uma nova cobrança legítima (duas cobranças iguais são duas cobranças);
- com a chave, reenviar a mesma chave e os mesmos dados devolve `200` com o lançamento já criado, sem
  novo lançamento nem nova notificação;
- a mesma chave com dados diferentes, outro membro ou o outro modo (geral/individual) devolve `409`;
- se o lançamento da operação foi excluído depois, o reenvio devolve `409`.

Recomendação para o frontend: gerar um UUID por intenção de cadastro (por formulário enviado) e
reutilizá-lo nas novas tentativas do mesmo envio.

## API REST

Autenticação: `Authorization: Bearer <access token>`, como no resto da API. As respostas trazem
`Cache-Control: no-store`.

### Listar pendências

`GET /api/Notificacoes/Pendentes?pageSize=20&cursor=<opaco>`

- Somente notificações não lidas do usuário do token, da mais recente para a mais antiga
  (`criadaEmUtc` decrescente; empate desfeito por `id`).
- `pageSize`: 1 a 50 (padrão 20). Fora disso: `400`.
- Paginação por cursor: envie em `cursor` o `proximoCursor` da página anterior. `proximoCursor: null`
  indica a última página. Cursor inválido: `400`. Marcar notificações como lidas entre uma página e
  outra não faz o cliente pular itens.
- `naoLidas` é o total de pendências do usuário, independente da página. Use-o no contador.
- Listar **não** marca nada como lido e não grava nada.

```json
{
  "items": [
    {
      "id": "6f1c2a9e-3b0d-4f7a-9c1e-2d4b5a6c7e8f",
      "tipo": "EventoCadastrado",
      "titulo": "Novo evento: Parque Ibirapuera",
      "mensagem": "Você está na lista de participantes do evento em Parque Ibirapuera, no dia 15/11/2026. Valor a pagar: R$ 20,00, com vencimento em 15/11/2026.",
      "criadaEmUtc": "2026-10-20T14:03:11.5170000Z",
      "lidaEmUtc": null,
      "eventoId": "0b7e5d1c-8a2f-4c3e-9d6b-1a2b3c4d5e6f",
      "lancamentoId": "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"
    }
  ],
  "naoLidas": 1,
  "pageSize": 20,
  "proximoCursor": null
}
```

Sem pendências: `{ "items": [], "naoLidas": 0, "pageSize": 20, "proximoCursor": null }`.

`eventoId` e `lancamentoId` são metadados e não dão acesso por si. O destinatário consulta o evento em
`GET /api/Me/Eventos/{eventoId}` (só enquanto participa dele; evento excluído, alheio ou do qual foi removido
devolve `404`), com data, local, composição do valor e a própria cobrança. `/api/Eventos` e `/api/Lancamentos`
continuam restritos às policies de gestão. O resumo também está em `titulo` e `mensagem`.

### Marcar como lida

`POST /api/Notificacoes/{id}/Leitura` (sem corpo)

- Chame **somente** quando o usuário abrir a notificação (ação explícita). Exibir a lista não conta
  como leitura.
- `200` só depois de gravar. Resposta: a notificação com `lidaEmUtc` e o novo total `naoLidas`.
- Idempotente: repetir devolve `200` com a mesma `lidaEmUtc` da primeira leitura. Leituras simultâneas
  (duas abas) também recebem a mesma data.
- `404` para notificação inexistente **ou** de outro usuário (a mesma resposta nos dois casos).
- A leitura nunca altera notificações de outros destinatários.

```json
{
  "notificacao": {
    "id": "6f1c2a9e-3b0d-4f7a-9c1e-2d4b5a6c7e8f",
    "tipo": "EventoCadastrado",
    "titulo": "Novo evento: Parque Ibirapuera",
    "mensagem": "…",
    "criadaEmUtc": "2026-10-20T14:03:11.5170000Z",
    "lidaEmUtc": "2026-10-20T14:05:42.0110000Z",
    "eventoId": "0b7e5d1c-8a2f-4c3e-9d6b-1a2b3c4d5e6f",
    "lancamentoId": "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"
  },
  "naoLidas": 0
}
```

Erros seguem Problem Details: `401` sem token ou sessão inválida, `400` validação, `404` não encontrada.

## Hub SignalR

- Caminho: `/hubs/notificacoes` (mesma origem da API, atrás do Nginx).
- Transportes: WebSocket e, se ele não estiver disponível, long polling. Server-Sent Events está
  desligado.
- Autenticação: o mesmo access token da API. O cliente oficial do SignalR envia o token no cabeçalho
  `Authorization` no `negotiate` e no long polling. No handshake WebSocket do navegador, que não aceita
  cabeçalhos, ele vai na query `access_token`. A API só aceita token na query no `GET` do handshake do
  hub. Em qualquer outra rota, a query é ignorada. Nginx, logs da API e telemetria não gravam essa query.
- Origem: navegadores de origem fora de `Cors:AllowedOrigins` recebem `403` no handshake WebSocket.
- O hub é só de saída: o cliente não chama métodos, não escolhe destinatário e não entra em grupos.
  Cada aviso vai apenas para as conexões do próprio usuário (todas as abas e dispositivos dele).
- A conexão é encerrada pelo servidor quando o access token expira (`CloseOnAuthenticationExpiration`)
  e, em até `Notificacoes:VerificacaoSessoesMs` (30 s), quando a sessão é revogada (logout, cargo
  inativo, usuário desativado, reuso de refresh token). O cliente reconecta com um token novo.

### Eventos

| Evento | Quando | Payload |
|---|---|---|
| `NotificacaoCriada` | uma notificação nova foi gravada para o usuário | `{ "notificacao": <mesmo objeto da API>, "naoLidas": 3 }` |
| `NotificacaoLida` | uma notificação do usuário foi marcada como lida (em qualquer aba ou dispositivo) | `{ "notificacaoId": "<guid>", "lidaEmUtc": "2026-10-20T14:05:42.011Z", "naoLidas": 2 }` |

`naoLidas` é o total no momento da emissão. Se outra mudança aconteceu depois, o próximo aviso ou a
próxima consulta à API corrige o valor.

### Sequência recomendada para o frontend

1. Criar a conexão com `accessTokenFactory` devolvendo o access token atual. Ele é chamado a cada
   conexão e reconexão, então após um refresh o token novo é usado.
2. Registrar os handlers (`NotificacaoCriada`, `NotificacaoLida`) **antes** de iniciar.
3. Iniciar a conexão (`start()`).
4. Só então chamar `GET /api/Notificacoes/Pendentes`. Assim nenhum aviso cai na janela entre a
   consulta e a conexão: o que chegar durante a consulta é mesclado pelo `id`.
5. Em `onreconnected` (e depois de um `start()` feito após `onclose`), consultar as pendências de novo.
   Avisos emitidos enquanto o usuário estava desconectado não são reenviados, mas a notificação está na
   API.

```ts
const conexao = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/notificacoes", { accessTokenFactory: () => obterAccessToken() })
  .withAutomaticReconnect()
  .build();

conexao.on("NotificacaoCriada", ({ notificacao, naoLidas }) => mesclar(notificacao, naoLidas));
conexao.on("NotificacaoLida", ({ notificacaoId, naoLidas }) => remover(notificacaoId, naoLidas));
conexao.onreconnected(() => recarregarPendencias());

await conexao.start();
await recarregarPendencias();
```

### Deduplicação e sincronização entre dispositivos

- Guarde as pendências num mapa por `id`. `NotificacaoCriada` com um `id` já conhecido não altera nada.
  Ignore também `NotificacaoCriada` de um `id` que já saiu da lista por leitura.
- `NotificacaoLida` remove o `id` da lista, inclusive em outras abas e dispositivos do mesmo usuário.
- Em caso de dúvida (aviso fora de ordem, reconexão, contador divergente), consulte a API: o estado do
  banco prevalece.
- A entrega é "pelo menos uma vez" enquanto o usuário estiver conectado. Não há garantia de entrega
  exatamente uma vez, nem confirmação de recebimento ou de leitura pelo SignalR.

### Abrir uma notificação

1. Mostrar o conteúdo (`titulo`, `mensagem`).
2. Chamar `POST /api/Notificacoes/{id}/Leitura`.
3. Remover a pendência e atualizar o contador **somente após o `200`**, usando `naoLidas` da resposta.
4. Se a chamada falhar (rede, `5xx`), manter a notificação como pendente e permitir nova tentativa.
   Repetir é seguro: a data da primeira leitura é preservada.
5. `404` significa que a notificação não existe para este usuário: remova-a da lista local.

## Confiabilidade

- Ao gravar a notificação, a API grava junto, na mesma linha, um **aviso pendente** (outbox na própria
  tabela `notificacoes`). A leitura grava o aviso `Lida` na mesma instrução que grava `LidaEmUtc`.
- O serviço `PublicadorAvisosNotificacoes` reserva os avisos pendentes já confirmados no banco
  (`UPDATE ... OUTPUT` com `READPAST`), emite pelo hub e limpa o aviso. Um commit acorda o serviço na
  hora; sem isso, ele varre o banco a cada `Notificacoes:VarreduraAvisosMs` (5 s). Em condições normais
  o aviso sai em milissegundos, dentro da meta de 15 s da issue.
- Falha ao emitir não desfaz o cadastro nem apaga a notificação. O aviso é tentado de novo com atraso
  exponencial (`AtrasoInicialAvisoMs` 5 s, até `AtrasoMaximoAvisoMs` 5 min), no máximo
  `MaxTentativasAviso` (10) vezes. Depois disso deixa de ser tentado. A notificação continua disponível
  na API.
- Se a API reiniciar com avisos pendentes ou reservados, eles são retomados depois do reinício (a
  reserva expira em `ReservaAvisoMs`, 30 s).
- Emitir sem nenhuma conexão aberta conclui o aviso. O usuário vê a notificação no próximo acesso.

Configuração (seção `Notificacoes`, todas com padrão no código, sem variável de ambiente nova):
`VarreduraAvisosMs`, `LoteAvisos`, `MaxTentativasAviso`, `AtrasoInicialAvisoMs`, `AtrasoMaximoAvisoMs`,
`ReservaAvisoMs`, `TempoLimiteEnvioMs` e `VerificacaoSessoesMs`.

## Infraestrutura e limitações

- **Uma instância da API.** O SignalR entrega aos clientes conectados na mesma instância que emite o
  aviso. A topologia atual tem uma instância por banco (Compose com um serviço `api` e o IIS local, e a
  rotação da senha SQL de runtime supõe uma instância). Com várias réplicas seria preciso um backplane
  (Redis ou Azure SignalR Service) ou sessões fixas, além de revisar a rotação da credencial. Os avisos
  continuam duráveis, e a API REST continua correta em qualquer réplica.
- **Nginx** (`nginx.conf`, `nginx.tls.conf`, `nginx.windows.conf`): o location do hub repassa
  `Upgrade`/`Connection`, usa `proxy_read_timeout 120s`, desliga buffering e grava access log só com o
  caminho (`$uri`), sem query. O error log desse location fica em `crit`, porque as linhas de erro do
  nginx repetem a linha da requisição com a query.
- **IIS (deploy Windows local):** o transporte WebSocket precisa do recurso "WebSocket Protocol" do IIS;
  sem ele, o cliente usa long polling, que envia o token no cabeçalho. O log W3C do IIS grava
  `cs-uri-query` por padrão e registraria o `access_token` do handshake WebSocket. O job `deploy-windows`
  ([`backend-deploy.yml`](../.github/workflows/backend-deploy.yml), passo "Garantir WebSocket no IIS e log
  sem query string") instala o recurso quando falta e tira `UriQuery` do log do site a cada deploy.
- **Docker/Linux:** o deploy constrói a imagem com `--pull`, para não reaproveitar uma imagem base antiga
  em cache. Instalação manual no Ubuntu: [`docs/instalacao-ubuntu.md`](instalacao-ubuntu.md).
- **Runtime:** o mínimo é ASP.NET Core 10.0.13, que corrige a CVE-2026-70159 (negação de serviço no
  SignalR). Esse mínimo é garantido automaticamente:
  - o `Dockerfile` falha no build se a imagem base `aspnet:10.0` em cache for mais antiga (refaça com
    `--pull`; o deploy Linux já usa);
  - o deploy Windows confere o runtime instalado no IIS antes de tirar o site do ar e para se ele for
    mais antigo;
  - os pacotes Microsoft do projeto estão alinhados em 10.0.13.
- **Texto em `varchar`:** título e mensagem usam a página de código 1252, como o resto do banco.
  Caracteres fora dela (por exemplo, emoji em um local ou descrição) aparecem como `?` na notificação.
