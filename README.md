# Desbravadores.Backend

Backend do projeto **Almirante**, responsável pela autenticação, consulta de usuários e gerenciamento de lançamentos financeiros dos Desbravadores. A aplicação é uma API HTTP construída com ASP.NET Core, persiste os dados no SQL Server por meio do Entity Framework Core e protege os recursos com autenticação JWT Bearer.

## Estado atual

O backend está em fase de **MVP funcional** e possui:

- login com e-mail e senha e emissão de token JWT;
- consulta dos dados do usuário autenticado;
- logout stateless — encerra a requisição, mas não revoga o token emitido;
- listagem de usuários;
- criação, listagem, atualização e exclusão de lançamentos financeiros;
- paginação, busca e filtros na listagem de lançamentos;
- validação dos dados recebidos e respostas de erro no padrão Problem Details;
- migrações do banco executadas na inicialização;
- carga inicial idempotente de um administrador e de lançamentos demonstrativos;
- Swagger/OpenAPI com suporte a Bearer Token, disponível em todos os ambientes;
- health checks de prontidão e atividade;
- telemetria com OpenTelemetry por meio do Service Defaults do Aspire;
- execução local com Docker Compose ou .NET Aspire;
- testes de integração da autenticação e dos lançamentos;
- integração contínua com GitHub Actions em runner self-hosted.

## Tecnologias

- .NET 10;
- ASP.NET Core Web API;
- Entity Framework Core 10;
- SQL Server 2022;
- JWT Bearer;
- Swagger / OpenAPI;
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
| `API_HOST_PORT` | porta HTTP da API publicada no host | `8090` |
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
| `POST` | `/api/Auth/login` | valida e-mail e senha e retorna o token e o usuário |
| `POST` | `/api/Auth/logout` | retorna `204 No Content`; não revoga o JWT |
| `GET` | `/api/Auth/Me` | retorna o usuário autenticado |
| `GET` | `/api/Usuarios` | lista os usuários ordenados por nome |
| `GET` | `/api/Lancamentos` | lista lançamentos com paginação e filtros |
| `POST` | `/api/Lancamentos` | cria um lançamento |
| `PUT` | `/api/Lancamentos/{id}` | atualiza os campos enviados de um lançamento |
| `DELETE` | `/api/Lancamentos/{id}` | exclui um lançamento |
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

## Banco de dados e seed

Na inicialização, a API aplica as migrações do Entity Framework Core. Em seguida:

1. cria o administrador configurado, caso ainda não exista um usuário com o mesmo e-mail normalizado;
2. inclui 20 lançamentos demonstrativos somente quando a tabela de lançamentos está vazia.

O processo é idempotente e pode ser executado novamente sem duplicar o administrador nem os dados demonstrativos já existentes.

## Testes

Execute a suíte a partir da raiz:

```bash
dotnet test backend/Almirante.slnx
```

Os testes utilizam o provedor em memória do Entity Framework Core e cobrem:

- login válido, inválido e requisição malformada;
- autorização dos endpoints protegidos;
- listagem, paginação e filtros;
- validação dos lançamentos;
- criação, atualização e exclusão;
- respostas `404 Not Found`.

## Integração contínua

O workflow `.github/workflows/backend-ci.yml` é executado em um runner **self-hosted** quando há:

- push para `main` com alterações em `backend/**` ou no próprio workflow;
- pull request direcionada à `main` com alterações nesses mesmos caminhos.

O pipeline utiliza o SDK .NET 10 já instalado no runner e executa:

```bash
dotnet restore backend/Almirante.slnx
dotnet build backend/Almirante.slnx --configuration Release --no-restore
dotnet test backend/Almirante.Api.Tests/Almirante.Api.Tests.csproj --configuration Release --no-build --verbosity normal
```

A execução atual realiza validação de compilação e testes. Ela ainda não publica automaticamente a aplicação no IIS.

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
