# Desbravadores.Backend

Backend do projeto **Almirante**, responsável pela autenticação, consulta de usuários e gerenciamento de lançamentos financeiros dos Desbravadores. A aplicação é uma API HTTP construída com ASP.NET Core, persiste os dados no SQL Server por meio do Entity Framework Core e protege os recursos com autenticação JWT Bearer.

## Estado atual

O backend está em fase de **MVP funcional** e possui:

- login com e-mail e senha e emissão de token JWT;
- consulta dos dados do usuário autenticado;
- logout stateless — encerra a requisição, mas não revoga o token emitido;
- listagem de usuários;
- criação, listagem, atualização e exclusão de lançamentos financeiros;
- lançamento geral idempotente para todos os usuários, com autorização por cargo, filtros de
  período, resumo financeiro (provisório) e exclusão lógica auditada por trigger;
- paginação, busca e filtros na listagem de lançamentos;
- validação dos dados recebidos e respostas de erro no padrão Problem Details;
- migrações do banco executadas na inicialização;
- carga inicial idempotente de um administrador e de lançamentos demonstrativos;
- Swagger/OpenAPI com suporte a Bearer Token, disponível em todos os ambientes;
- health checks de prontidão e atividade;
- telemetria com OpenTelemetry por meio do Service Defaults do Aspire;
- Nginx como reverse proxy no Docker Compose, com rate limit no login e encaminhamento do IP real do cliente;
- execução local com Docker Compose ou .NET Aspire;
- testes de integração da autenticação, dos lançamentos e do encaminhamento de IP real;
- integração contínua com GitHub Actions em runner self-hosted.

## Tecnologias

- .NET 10;
- ASP.NET Core Web API;
- Entity Framework Core 10;
- SQL Server 2022;
- JWT Bearer;
- Swagger / OpenAPI;
- Nginx (reverse proxy e rate limit, no Docker Compose);
- .NET Aspire;
- OpenTelemetry;
- xUnit e WebApplicationFactory;
- Docker e Docker Compose;
- GitHub Actions.

## Estrutura do repositório

```text
.
├── .github/
│   └── workflows/
│       └── backend-ci.yml          # restore, build e testes do backend
├── backend/
│   ├── Almirante.Api/              # API, regras, persistência e migrações
│   ├── Almirante.Api.Tests/        # testes de integração
│   ├── Almirante.AppHost/          # orquestração local com Aspire
│   ├── Almirante.ServiceDefaults/  # health checks, telemetria e service discovery
│   └── Almirante.slnx
├── nginx/
│   └── nginx.conf                  # reverse proxy e rate limit do login (Docker Compose)
├── compose.yaml
├── .env.example
└── README.md
```

## Executar com Docker Compose

### Pré-requisitos

- Docker com o comando `docker compose` disponível.

### Configuração

Na raiz do repositório, crie o arquivo de configuração local:

```bash
cp .env.example .env
```

Altere no `.env`, no mínimo, os valores de:

- `SQL_SA_PASSWORD`;
- `JWT_KEY`;
- `SEED_ADMIN_SENHA`.

O arquivo `.env` contém segredos locais e não deve ser versionado.

### Inicialização

```bash
docker compose up --build
```

Com os valores do `.env.example`, os serviços ficam disponíveis em:

- API: `http://localhost:8090`;
- Swagger: `http://localhost:8090/swagger`;
- readiness: `http://localhost:8090/health`;
- liveness: `http://localhost:8090/alive`;
- SQL Server: `localhost,14330`.

O Compose utiliza o volume nomeado `almirante-sqlserver-data` para persistir os dados do SQL Server.

Para encerrar os contêineres sem apagar os dados:

```bash
docker compose down
```

Para também remover o volume persistente:

```bash
docker compose down -v
```

## Arquitetura com Nginx (Docker Compose)

No Docker Compose, o Nginx atua como reverse proxy e único ponto de entrada HTTP externo:

```text
Cliente
  |
  v
Nginx (:8090 -> :80)
  |
  v
API (rede interna, :8080)
  |
  v
SQL Server
```

A API deixou de publicar porta diretamente no host (não existe mais `ports: 8090:8080` no serviço
`api`); ela só é alcançável pela rede interna do Compose (`almirante-net`), pelo nginx. Isso evita
que alguém acesse a API diretamente e contorne o rate limit do login. A porta externa continua
sendo `8090` (variável `API_HOST_PORT`), agora publicada pelo nginx.

### Rate limit do login

`POST /api/Auth/login` (e variações de caixa equivalentes, como `/api/auth/login`) tem rate limit
aplicado pelo Nginx, configurado em `nginx/nginx.conf`:

- até **3 tentativas imediatas por IP** em uma janela de **60 segundos**, sem atraso artificial;
- a partir da 4ª tentativa na mesma janela, a resposta é `429 Too Many Requests`, com corpo:

  ```json
  {
    "title": "Muitas tentativas de login.",
    "status": 429,
    "detail": "Aguarde antes de tentar realizar o login novamente."
  }
  ```

  com `Content-Type: application/problem+json` e o header `Retry-After: 60` (só nessa resposta);
- passado o período de recuperação (~60s), novas tentativas voltam a ser permitidas;
- nenhum outro endpoint (`/api/Auth/Me`, `/api/Usuarios`, `/api/Lancamentos`, `/health`, `/alive`,
  Swagger etc.) é afetado por esse limite.

Para alterar o limite ou a janela, edite as diretivas `rate=` (na `limit_req_zone`) e `burst=` (no
`location` do login) em `nginx/nginx.conf` — os comentários no próprio arquivo explicam a relação
entre os dois valores.

### Encaminhamento do IP real e proteção contra spoofing

O Nginx encaminha para a API os headers `Host`, `X-Real-IP`, `X-Forwarded-For` e
`X-Forwarded-Proto`. A API usa `ForwardedHeadersMiddleware` (`app.UseForwardedHeaders()`, em
`Program.cs`, antes de `UseHttpsRedirection()` e de qualquer middleware de autenticação/autorização)
para traduzir esses headers, de forma que `HttpContext.Connection.RemoteIpAddress` passe a
representar o IP real do cliente — disponível para uso futuro em auditoria, bloqueio por IP etc.

Esse encaminhamento só é confiável porque a API não aceita `X-Forwarded-For`/`X-Forwarded-Proto`
de qualquer origem: a configuração (`ReverseProxy:TrustedNetworkCidr`, variável de ambiente
`API_TRUSTED_PROXY_CIDR`, padrão `172.30.0.0/24` — a subnet da rede `almirante-net`) restringe a
confiança apenas à conexão que vem dessa rede interna (o próprio container do nginx), com
`ForwardLimit = 1`. Na prática:

- um cliente que envia `X-Forwarded-For: 1.2.3.4` diretamente para o Nginx **não consegue** fazer a
  API acreditar que esse é o IP dele: o Nginx anexa o IP real observado por ele ao final do header
  (`proxy_add_x_forwarded_for`), e a API só usa o valor mais à direita — o que o Nginx efetivamente
  viu na conexão;
- se alguém acessar a API diretamente (contornando o Nginx), a conexão não vem da rede confiável e
  o header é ignorado por completo; `RemoteIpAddress` reflete o IP real de quem conectou.

### Docker/Nginx no Windows (IIS)

O deploy Windows (IIS, via self-hosted runner) tem a mesma proteção, mas sem Docker: o job
`deploy-windows` (`.github/workflows/backend-deploy.yml`) instala o Nginx nativo para Windows em
`C:\nginx` (uma vez só; nos deploys seguintes só atualiza a config) e o registra numa Tarefa
Agendada do Windows para iniciar sozinho com a máquina.

```text
Cliente
  |
  v
Nginx (127.0.0.1:8090)
  |
  v
IIS (127.0.0.1:<porta do site>, loopback)
```

Diferenças em relação ao Docker Compose:

- o nginx roda como processo nativo (`nginx.exe`), não em container; a config fica em
  `nginx/nginx.windows.conf` no repositório (mesmo rate limit de login do `nginx/nginx.conf`), com
  um placeholder `__IIS_PORT__` substituído pelo workflow pela porta real do site no IIS (descoberta
  dinamicamente a cada deploy — essa porta já mudou no passado, ver comentários no workflow);
- o binding do site no IIS é só em `127.0.0.1` (loopback): não é alcançável de fora desta máquina
  diretamente, só através do nginx. Esse deploy Windows é usado apenas localmente/rede interna — o
  deploy público de fato é o Linux (Docker Compose);
- `ReverseProxy:TrustedNetworkCidr` é configurado como `127.0.0.1/32` em
  `appsettings.Production.json` (arquivo local ao servidor, fora do controle de versão, preservado
  entre deploys), já que aqui o nginx e a API rodam na mesma máquina — diferente do Docker, onde a
  confiança é numa subnet inteira.

O `.NET Aspire` (seção abaixo) continua executando a API diretamente, sem Nginx, em desenvolvimento
local — a proteção de rate limit só se aplica aos dois caminhos de deploy (Docker Compose e IIS).

## Executar com .NET Aspire

### Pré-requisitos

- SDK do .NET 10;
- runtime de contêiner compatível com o Aspire.

Execute o AppHost:

```bash
dotnet run --project backend/Almirante.AppHost
```

O AppHost:

- inicia um SQL Server em contêiner;
- utiliza o volume persistente `almirante-sqlserver-data`;
- cria o banco lógico `almirante`;
- aguarda o banco ficar disponível;
- inicia a API e exibe os endereços dos recursos no painel do Aspire.

As configurações de desenvolvimento incluem credenciais locais para o administrador inicial:

- e-mail: `admin@local.dev`;
- senha: `senha`.

Esses valores são exclusivos para desenvolvimento e devem ser substituídos em qualquer outro ambiente.

## Variáveis de ambiente

O arquivo `.env.example` é consumido pelo Docker Compose e documenta as configurações disponíveis:

| Variável | Finalidade | Padrão no Compose |
| --- | --- | --- |
| `SQL_SA_PASSWORD` | senha do usuário `sa` do SQL Server | obrigatória |
| `SQL_HOST_PORT` | porta do SQL Server publicada no host | `14330` |
| `API_HOST_PORT` | porta HTTP publicada no host pelo nginx (reverse proxy da API) | `8090` |
| `API_TRUSTED_PROXY_CIDR` | rede (CIDR) confiável para os headers X-Forwarded-For/X-Forwarded-Proto enviados pelo nginx; deve corresponder à subnet de `almirante-net` no `compose.yaml` | `172.30.0.0/24` |
| `ASPNETCORE_ENVIRONMENT` | ambiente da aplicação | `Production` |
| `JWT_ISSUER` | emissor do token JWT | `Almirante.Api` |
| `JWT_AUDIENCE` | audiência do token JWT | `Almirante.Frontend` |
| `JWT_KEY` | chave de assinatura e validação do JWT | obrigatória |
| `JWT_EXPIRATION_MINUTES` | duração do token em minutos | `60` |
| `SEED_ADMIN_NOME` | nome do administrador inicial | `Administrador` |
| `SEED_ADMIN_EMAIL` | e-mail do administrador inicial | `admin@local.dev` |
| `SEED_ADMIN_SENHA` | senha do administrador inicial | obrigatória |
| `CORS_ORIGIN_1` | primeira origem permitida pelo CORS | `http://localhost:4200` |
| `CORS_ORIGIN_2` | segunda origem permitida pelo CORS | `http://localhost:4201` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | endpoint opcional para exportação OTLP | não definido |

A `JWT_KEY` deve possuir pelo menos 32 caracteres e ser diferente em cada ambiente.

## Autenticação

Faça login com o administrador criado pelo seed:

```bash
curl --request POST http://localhost:8090/api/Auth/login \
  --header 'Content-Type: application/json' \
  --data '{"email":"admin@local.dev","senha":"senha"}'
```

A resposta contém `token.accessToken`, `token.expiresAtUtc` e os dados do usuário. Nos endpoints protegidos, envie:

```http
Authorization: Bearer SEU_TOKEN
```

O logout atual não mantém blacklist nem sessão persistida. Portanto, um JWT válido continua utilizável até expirar.

## Endpoints

Com exceção do login, do Swagger e dos health checks, todos os endpoints exigem um token JWT válido.

| Método | Rota | Comportamento |
| --- | --- | --- |
| `POST` | `/api/Auth/login` | valida e-mail e senha e retorna o token e o usuário; sujeito a rate limit do Nginx no Docker Compose (ver [Arquitetura com Nginx](#arquitetura-com-nginx-docker-compose)) |
| `POST` | `/api/Auth/logout` | retorna `204 No Content`; não revoga o JWT |
| `GET` | `/api/Auth/Me` | retorna o usuário autenticado |
| `GET` | `/api/Usuarios` | lista os usuários ordenados por nome |
| `GET` | `/api/Lancamentos` | lista lançamentos com paginação e filtros |
| `POST` | `/api/Lancamentos` | cria um lançamento |
| `PUT` | `/api/Lancamentos/{id}` | atualiza os campos enviados de um lançamento |
| `DELETE` | `/api/Lancamentos/{id}` | exclui (fisicamente) um lançamento |
| `POST` | `/api/Lancamentos/Geral` | lançamento geral: cria o mesmo lançamento para todos os usuários elegíveis (idempotente) — ver [Lançamento geral](#lançamento-geral) |
| `GET` | `/api/Lancamentos/Geral` | lista os lançamentos ativos criados por lançamento geral, com filtro de período e resumo financeiro |
| `DELETE` | `/api/Lancamentos/Geral/{id}` | exclui logicamente (nunca fisicamente) um lançamento do escopo do lançamento geral, com motivo obrigatório e auditoria |
| `GET` | `/health` | informa a prontidão da aplicação |
| `GET` | `/alive` | informa se a aplicação está ativa |

### Consulta de lançamentos

`GET /api/Lancamentos` aceita:

| Parâmetro | Comportamento |
| --- | --- |
| `page` | página solicitada; valores menores que `1` são convertidos para `1` |
| `pageSize` | quantidade por página; padrão `10` e máximo `100` |
| `search` | busca em membro, descrição, tipo, categoria e status |
| `status` | filtra por status; `Todos` não aplica o filtro |
| `tipo` | filtra por tipo; `Todos` não aplica o filtro |
| `data` | filtra o vencimento no formato `yyyy-MM-dd` |

Exemplo:

```bash
curl 'http://localhost:8090/api/Lancamentos?page=1&pageSize=10&tipo=Campori' \
  --header 'Authorization: Bearer SEU_TOKEN'
```

A resposta possui `items`, `total`, `page`, `pageSize` e `totalPages`. Os itens são ordenados pelo vencimento, do mais recente para o mais antigo.

### Dados de lançamentos

Na criação, são obrigatórios:

- `membroNome`;
- `tipo`;
- `categoria`;
- `vencimento`;
- `status`.

`membroId`, `descricao` e `moeda` são opcionais. Quando a moeda não é informada, a API utiliza `BRL`. O valor não pode ser negativo.

Valores aceitos:

- tipos: `Mensalidade`, `Campori`, `Acampamento`, `Uniflash`, `Doação`, `Evento` e `Outros`;
- categorias: `Clube` e `Evento`;
- status: `Pago`, `Pendente` e `Atrasado`;
- vencimento: formato `yyyy-MM-dd`.

Exemplo de criação:

```bash
curl --request POST http://localhost:8090/api/Lancamentos \
  --header 'Authorization: Bearer SEU_TOKEN' \
  --header 'Content-Type: application/json' \
  --data '{
    "membroNome": "Nome do membro",
    "tipo": "Mensalidade",
    "descricao": "Mensalidade do clube",
    "categoria": "Clube",
    "valor": 25.00,
    "moeda": "BRL",
    "vencimento": "2026-08-10",
    "status": "Pendente"
  }'
```

## Lançamento geral

Cria, numa única operação consistente, o mesmo lançamento para **todos os usuários cadastrados**
(`POST /api/Lancamentos/Geral`), lista os lançamentos ativos gerados por essa operação
(`GET /api/Lancamentos/Geral`) e permite excluir logicamente um item específico desse escopo
(`DELETE /api/Lancamentos/Geral/{id}`). É um escopo separado do CRUD genérico acima: usa as
mesmas tabelas (`Lancamentos`), mas nunca mistura os dois — lançamentos criados aqui têm um
`OperacaoId` preenchido; os criados pelo CRUD genérico, não.

### Autorização

Os três endpoints exigem um token Bearer autenticado com uma das roles `ADM`, `DIR`, `DIRA`,
`SEC` ou `TES`. Ao contrário do restante da API, qualquer falha de autorização neste escopo — sem
token, token inválido/expirado, ou autenticado sem uma dessas roles — retorna sempre:

```http
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json

{ "title": "ACESSO NEGADO!", "status": 401 }
```

### Criar (`POST /api/Lancamentos/Geral`)

Requer o header `Idempotency-Key` (string não vazia, definida pelo cliente, **diferente do token
JWT**) além do corpo:

```bash
curl --request POST http://localhost:8090/api/Lancamentos/Geral \
  --header 'Authorization: Bearer SEU_TOKEN' \
  --header 'Idempotency-Key: 5b1f7e2a-9c3d-4e51-8b7a-1234567890ab' \
  --header 'Content-Type: application/json' \
  --data '{
    "tipo": "Mensalidade",
    "categoria": "Clube",
    "valor": 25.00,
    "vencimento": "2026-12-10"
  }'
```

Resposta (`200 OK`):

```json
{
  "operacaoId": "9f2c9e10-...",
  "usuariosProcessados": 42,
  "lancamentosCriados": 42,
  "dataHoraUtc": "2026-12-01T12:00:00Z"
}
```

- **Usuários elegíveis**: hoje, todo usuário cadastrado em `Usuarios`. O domínio não define, em
  nenhum outro lugar do sistema, uma regra de elegibilidade diferente (`Usuario` não tem flag de
  status/ativo, nem distinção por cargo usada para esse fim) — esta é uma decisão documentada, não
  um requisito explícito da issue; ajustar em `LancamentosGeraisService.CreateAsync` se surgir uma
  regra diferente (ex.: restringir ao cargo `DS`).
- **Idempotência**: reenviar a mesma `Idempotency-Key` com o mesmo corpo devolve `200` com a
  mesma resposta original, sem criar novos lançamentos. A mesma chave com corpo diferente (tipo,
  categoria, valor ou vencimento) devolve `409 Conflict`. A garantia é persistida (tabela
  `LancamentosOperacoes`, chave única) e vale sob concorrência real (não apenas no mesmo processo).
- **Consistência**: a operação inteira (registro de idempotência + todos os lançamentos) é
  persistida em uma única transação — todos os lançamentos são criados, ou nenhum é.
- Lançamentos criados entram com `status: "Pendente"`, `moeda: "BRL"` e `ativo: true`.

### Listar (`GET /api/Lancamentos/Geral`)

Retorna somente lançamentos ativos (`Ativo = 1`) deste escopo, filtrados por vencimento — o
domínio não define outra data para orientar esta listagem, então **vencimento** foi escolhido por
representar quando a cobrança é devida. `Vencimento` é `date` (sem hora/fuso), então o dia inicial
e o final do intervalo entram por completo, sem qualquer perda por causa de horário.

| Parâmetro | Comportamento |
| --- | --- |
| (nenhum) | equivalente a `periodo=30` |
| `periodo=30` \| `60` \| `90` | hoje e os N−1 dias anteriores (data UTC do servidor) |
| `periodo=personalizado&dataInicio=AAAA-MM-DD&dataFim=AAAA-MM-DD` | intervalo informado; as duas datas são obrigatórias e `dataInicio` não pode ser posterior a `dataFim` |

Período desconhecido, datas em formato inválido, período personalizado sem as duas datas, ou
`dataInicio` posterior a `dataFim` retornam `400 Bad Request`.

```bash
curl 'http://localhost:8090/api/Lancamentos/Geral?periodo=90' \
  --header 'Authorization: Bearer SEU_TOKEN'
```

```json
{
  "lancamentos": [ { "id": "...", "tipo": "Mensalidade", "valor": 25.00, "vencimento": "2026-12-10", "status": "Pendente", "...": "..." } ],
  "resumo": { "totalDespesas": 800.00, "totalEntradas": 1000.00, "saldoAtual": 200.00 }
}
```

`resumo` é **provisório**: valores fixos, sempre os mesmos independentemente do período ou da
existência de lançamentos (inclusive com `lancamentos: []`). Ver
`LancamentosGeraisService.ResumoFixoProvisorio` — substituir por um cálculo real quando a regra de
negócio (o que conta como despesa/entrada) for definida.

### Excluir (`DELETE /api/Lancamentos/Geral/{id}`)

Exclusão **lógica** (`UPDATE ... SET Ativo = 0`), nunca `DELETE` físico. Exige motivo no corpo:

```bash
curl --request DELETE http://localhost:8090/api/Lancamentos/Geral/COLE_AQUI_O_ID \
  --header 'Authorization: Bearer SEU_TOKEN' \
  --header 'Content-Type: application/json' \
  --data '{ "motivo": "Lançamento cadastrado incorretamente." }'
```

- `motivo` é obrigatório, validado após `Trim()`, entre 1 e 255 caracteres; vazio, só espaços, ou
  maior que 255 caracteres retornam `400` (nunca truncado silenciosamente).
- Lançamento inexistente **ou já inativo** retorna `404 Not Found` — uma segunda tentativa de
  excluir o mesmo item não gera nova auditoria (o `UPDATE` não afeta nenhuma linha, então o
  trigger de auditoria, abaixo, nem chega a rodar).
- Sucesso retorna `204 No Content` e afeta somente o lançamento indicado, nunca o lote inteiro.

#### Auditoria (trigger no banco)

Toda transição `Ativo: true -> false` é auditada pelo trigger
`TR_Lancamentos_AuditoriaExclusaoLogica` (SQL Server) na tabela `lancamentos_deletados`, com o
responsável pela exclusão, IP, motivo, data/hora UTC (gerada pelo banco) e um snapshot do
lançamento antes da exclusão. O usuário responsável e o IP nunca são aceitos do frontend: chegam
via `SESSION_CONTEXT`, alimentado pela API na mesma conexão/transação do `UPDATE`
(`LancamentosGeraisService.DeleteAsync`) a partir das claims JWT validadas e do
`HttpContext.Connection.RemoteIpAddress` (que já respeita `ReverseProxy:TrustedNetworkCidr`, ver
[Encaminhamento do IP real](#encaminhamento-do-ip-real-e-proteção-contra-spoofing)). Sem esse
contexto, o próprio trigger rejeita a desativação (a transação inteira é revertida).

Essa auditoria só existe em SQL Server real — o provider EF Core InMemory usado no restante da
suíte de testes não tem triggers nem `SESSION_CONTEXT` (ver
[Testes](#testes) sobre `LancamentosGeraisAuditoriaSqlServerTests`).

## Banco de dados e seed

Na inicialização, a API aplica as migrações do Entity Framework Core. Em seguida:

1. cria o administrador configurado, caso ainda não exista um usuário com o mesmo e-mail normalizado;
2. inclui 20 lançamentos demonstrativos somente quando a tabela de lançamentos está vazia.

O processo é idempotente e pode ser executado novamente sem duplicar o administrador nem os dados demonstrativos já existentes.

## Testes

Execute a suíte a partir da raiz (exclui os testes que exigem Docker, ver abaixo):

```bash
dotnet test backend/Almirante.slnx --filter "Category!=RequiresDocker"
```

A maior parte dos testes utiliza o provedor em memória do Entity Framework Core e cobre:

- login válido, inválido e requisição malformada;
- autorização dos endpoints protegidos;
- listagem, paginação e filtros;
- validação dos lançamentos;
- criação, atualização e exclusão;
- lançamento geral (`LancamentosGeraisTests`): autorização (as 5 roles permitidas e a mensagem
  exata `ACESSO NEGADO!`), idempotência (reenvio, conflito, consistência), filtros de período,
  resumo fixo e exclusão lógica — tudo o que não depende do trigger/SQL Server real;
- configuração do `ForwardedHeadersMiddleware` (`ForwardedHeadersTests`): X-Forwarded-For/X-Forwarded-Proto
  só são aceitos quando a conexão imediata vem da rede confiável configurada, e um valor forjado à
  esquerda do header (simulando o encadeamento do Nginx) é ignorado;
- respostas `404 Not Found`.

### Testes contra SQL Server real (`LancamentosGeraisAuditoriaSqlServerTests`)

O provedor EF Core InMemory não tem triggers, `SESSION_CONTEXT` nem impõe índices únicos entre
instâncias de `DbContext` de forma equivalente ao SQL Server — então o comportamento do trigger de
auditoria da exclusão lógica do lançamento geral (usuário/IP/motivo/snapshot, atualização de
múltiplas linhas, rollback sem contexto válido, ausência de vazamento de `SESSION_CONTEXT` entre
requisições) e a idempotência sob concorrência real só podem ser comprovados contra um banco de
verdade. `LancamentosGeraisAuditoriaSqlServerTests` sobe um SQL Server 2022 real via
[Testcontainers](https://dotnet.testcontainers.org/) (a mesma imagem do `compose.yaml`) e roda as
migrações reais contra ele.

Requer Docker disponível na máquina. Por isso fica marcado com `[Trait("Category",
"RequiresDocker")]` e é excluído do `dotnet test` acima e do CI (`backend-ci.yml`), que roda num
runner self-hosted sem Docker configurado. Para rodar localmente:

```bash
dotnet test backend/Almirante.Api.Tests --filter "Category=RequiresDocker"
```

### Cenários manuais do rate limit do Nginx

O rate limit em si (`limit_req` do Nginx) não é coberto pela suíte automatizada acima — ela roda
sobre `WebApplicationFactory`, sem o Nginx. Depois de `docker compose up --build`, valide
manualmente contra `http://localhost:8090` (ajuste para o valor de `API_HOST_PORT` se alterado):

1. **Login válido** — 1ª tentativa com credenciais corretas → `200 OK` e token JWT.
2. **Credenciais inválidas** — 1ª tentativa incorreta → `401 Unauthorized` (nunca `429`).
3. **Três tentativas** — 3 requisições de login em sequência, do mesmo IP → todas chegam à API
   (com credenciais erradas: `401`, `401`, `401`).
4. **Quarta tentativa** — uma 4ª requisição imediatamente depois → `429 Too Many Requests`; essa
   requisição não chega ao `AuthController` (pode ser confirmado pela ausência de log da API).
5. **Recuperação** — aguarde ~60s e tente novamente → volta a ser permitido.
6. **Isolamento por IP** — IPs diferentes têm buckets independentes; atingir o limite em um IP não
   afeta outro (pode ser testado com `--header 'X-Forwarded-For: ...'` mudando o IP de origem só se
   o teste for feito a partir de fora da rede confiável — dentro da rede confiável, ver cenário 7).
7. **Tentativa de spoofing** — enviar `--header 'X-Forwarded-For: 1.1.1.1'` não muda o IP usado pelo
   rate limit (que usa a conexão TCP real vista pelo Nginx) nem o IP confiável pela API.
8. **Endpoints normais** — mesmo após atingir o limite do login, `GET /health` e os demais
   endpoints continuam respondendo normalmente.

Exemplo de execução dos cenários 3 e 4:

```bash
for i in 1 2 3 4; do
  curl -s -o /dev/null -w "tentativa $i: %{http_code}\n" \
    --request POST http://localhost:8090/api/Auth/login \
    --header 'Content-Type: application/json' \
    --data '{"email":"admin@local.dev","senha":"senha-errada"}'
done
```

## Integração contínua

O workflow `.github/workflows/backend-ci.yml` é executado em um runner **self-hosted** quando há:

- push para `main` com alterações em `backend/**` ou no próprio workflow;
- pull request direcionada à `main` com alterações nesses mesmos caminhos.

O pipeline utiliza o SDK .NET 10 já instalado no runner e executa:

```bash
dotnet restore backend/Almirante.slnx
dotnet build backend/Almirante.slnx --configuration Release --no-restore
dotnet test backend/Almirante.Api.Tests/Almirante.Api.Tests.csproj --configuration Release --no-build --verbosity normal --filter "Category!=RequiresDocker"
```

A execução atual realiza validação de compilação e testes. O filtro exclui
`LancamentosGeraisAuditoriaSqlServerTests` (exige Docker, indisponível neste runner) — ver
[Testes contra SQL Server real](#testes-contra-sql-server-real-lancamentosgeraisauditoriasqlservertests).

O workflow `.github/workflows/backend-deploy.yml` cuida da publicação em si, em runners self-hosted, a cada push em `develop`: builda e sobe os containers via Docker Compose no(s) Pop!_OS registrado(s) e publica a aplicação no IIS na máquina Windows. Em ambos os casos, as migrations pendentes rodam automaticamente na inicialização da aplicação (`DbSeeder.SeedAsync`), e o workflow só reporta sucesso quando o endpoint `/health` responde.

## Observabilidade e saúde

O projeto `Almirante.ServiceDefaults` configura:

- logs, métricas e traces com OpenTelemetry;
- instrumentação do ASP.NET Core, HttpClient e runtime;
- exportação OTLP quando `OTEL_EXPORTER_OTLP_ENDPOINT` estiver configurado;
- service discovery;
- resiliência padrão para clientes HTTP;
- `/health` para readiness;
- `/alive` para liveness.

As requisições aos health checks são excluídas dos traces.

## Swagger

O Swagger UI está disponível em `/swagger` em todos os ambientes, inclusive quando a API é executada como `Production` no IIS.

Como a documentação expõe o contrato da API, essa decisão é adequada ao MVP interno atual. Antes de uma exposição pública, recomenda-se restringir o acesso por autenticação, rede ou configuração de ambiente.
