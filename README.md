# Desbravadores.Backend

Backend do projeto **Almirante**, responsável pela autenticação, consulta de usuários e gerenciamento de lançamentos financeiros dos Desbravadores. A aplicação é uma API HTTP construída com ASP.NET Core, persiste os dados no SQL Server por meio do Entity Framework Core e protege os recursos com autenticação JWT Bearer.

## Estado atual

O backend está em fase de **MVP funcional** e possui:

- login com e-mail e senha e emissão de token JWT;
- consulta dos dados do usuário autenticado;
- sessões persistidas, refresh rotativo e logout com revogação efetiva;
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
- `JWT_KEY_V1` (Base64 de no mínimo 32 bytes) e `JWT_ACTIVE_KEY_ID`;
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
| `JWT_ACTIVE_KEY_ID` | `kid` usado para novas assinaturas | `v1` |
| `JWT_KEY_V1` | chave HS256 Base64 associada a `v1` | obrigatória |
| `JWT_ACCESS_TOKEN_MINUTES` | duração máxima do access token | `10` |
| `AUTH_SESSION_DAYS` | limite absoluto da sessão | `7` |
| `AUTH_REFRESH_INACTIVITY_HOURS` | inatividade máxima entre login/refresh | `24` |
| `SEED_ADMIN_NOME` | nome do administrador inicial | `Administrador` |
| `SEED_ADMIN_EMAIL` | e-mail do administrador inicial | `admin@local.dev` |
| `SEED_ADMIN_SENHA` | senha do administrador inicial | obrigatória |
| `CORS_ORIGIN_1` | primeira origem permitida pelo CORS | `http://localhost:4200` |
| `CORS_ORIGIN_2` | segunda origem permitida pelo CORS | `http://localhost:4201` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | endpoint opcional para exportação OTLP | não definido |

A chave decodificada deve possuir pelo menos 32 bytes aleatórios e ser diferente em cada ambiente.

## Autenticação

Faça login com o administrador criado pelo seed:

```bash
curl --request POST https://localhost:8090/api/Auth/login \
  --header 'X-CSRF-TOKEN: TOKEN_OBTIDO_EM_/api/Auth/csrf' \
  --header 'Content-Type: application/json' \
  --data '{"email":"admin@local.dev","senha":"senha"}'
```

A resposta contém somente `token.accessToken` e `token.expiresAtUtc`; consulte o perfil atual em `/api/Auth/Me`. Nos endpoints protegidos, envie:

```http
Authorization: Bearer SEU_TOKEN
```

O logout revoga persistentemente a sessão apresentada (tabela `AuthSession` no SQL Server); o refresh cookie é removido e um novo login é exigido.

O antiforgery do ASP.NET Core vincula o CSRF ao usuário autenticado no momento em que ele foi emitido. Se o cliente envia `Authorization: Bearer` em toda requisição, peça um novo `GET /api/Auth/csrf` depois do login antes de chamar `refresh`/`logout` com esse header — reaproveitar o CSRF obtido antes do login resulta em `400` (ver [`docs/authentication-security.md`](docs/authentication-security.md)).

| Método | Rota | Comportamento |
| --- | --- | --- |
| `GET` | `/api/Auth/csrf` | emite a proteção antiforgery para os fluxos com cookie |
| `POST` | `/api/Auth/login` | valida credenciais, cria sessão e retorna somente o access token |
| `POST` | `/api/Auth/refresh` | rotaciona o refresh cookie e retorna novo access token |
| `POST` | `/api/Auth/logout` | revoga persistentemente a sessão apresentada e remove o cookie |
| `GET` | `/api/Auth/Me` | retorna o usuário autenticado |
| `GET` | `/api/Usuarios` | lista os usuários ordenados por nome |
| `GET` | `/health` | informa a prontidão da aplicação |
| `GET` | `/alive` | informa se a aplicação está ativa |

## Lançamentos

O agregado financeiro possui um único fluxo de criação, protegido pelas roles `ADM`, `DIR`,
`DIRA`, `SEC` e `TES`.

| Método | Rota | Finalidade |
|---|---|---|
| `GET` | `/api/Lancamentos` | Lista somente lançamentos ativos, com paginação e filtros |
| `POST` | `/api/Lancamentos/Registrar` | Registra para um membro ou, atomicamente, para todos |
| `PUT` | `/api/Lancamentos/{id}` | Atualiza campos permitidos |
| `DELETE` | `/api/Lancamentos/{id}` | Exclusão lógica auditada, com motivo |

O contrato canônico de criação é:

```json
{
  "membroId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "finalidade": "Mensalidade",
  "descricao": "Mensalidade de outubro",
  "categoria": "Clube",
  "tipoFluxo": "Entrada",
  "valor": 50.00,
  "vencimento": "2026-10-10",
  "aplicarATodosOsMembros": false
}
```

`membroId` é obrigatório no modo individual e deve ser nulo no modo geral. O servidor obtém o
nome por `Usuarios`, define novos lançamentos como `Pendente` e trabalha exclusivamente em BRL;
por isso nome, status e moeda não fazem parte do request. `Vencimento` é `DateOnly` e não aceita
datas passadas.

No modo geral (`aplicarATodosOsMembros: true`), `Idempotency-Key` é obrigatório. Uma única
operação carrega os IDs elegíveis, insere o lote com `AddRange`/`SaveChanges` atômico e registra
`OperacaoId`. Repetir chave e payload devolve a operação existente; mudar o payload resulta em
`409 Conflict`. O hash inclui finalidade, descrição, categoria, fluxo, valor e vencimento.

`DELETE` recebe `{ "motivo": "..." }`, apenas muda `Ativo` para `false` e mantém a auditoria por
trigger/`SESSION_CONTEXT`. Listagens projetam o nome com JOIN, sem armazená-lo em `Lancamentos` e
sem N+1. `Finalidade` (Mensalidade, Campori etc.) é a natureza; `TipoFluxo` (Entrada/Despesa) é a
direção financeira, portanto ambos permanecem.

## Banco de dados e migrations

Migrations são aplicadas incrementalmente no startup. `UnifyLancamentosFlow` renomeia `Tipo` para
`Finalidade`, remove `MembroNome`/`Moeda`, cria a FK restritiva para `Usuarios`, atualiza índices e
o trigger de auditoria sem apagar o histórico de migrations.

## Testes

```bash
dotnet test backend/Almirante.slnx
```

A suíte cobre contrato e validação, membro inexistente, criação individual e geral, idempotência,
conflito de chave e soft delete.

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

## Segurança da autenticação (issue #29)

O login agora retorna somente o envelope `token`; o perfil atual vem de `GET /api/Auth/Me`. Sessões e hashes de refresh tokens são persistidos no SQL Server, refresh é rotativo por cookie seguro e logout revoga a sessão apresentada. O fluxo de frontend, configuração Base64/kid, rotação, migração, TLS e riscos residuais estão em [`docs/authentication-security.md`](docs/authentication-security.md). Tokens emitidos antes desta mudança não têm `sid` e exigem novo login.
