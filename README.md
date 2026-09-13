# Desbravadores.Backend

Backend do projeto **Almirante**, uma API HTTP para autenticação, consulta de usuários e gerenciamento de lançamentos financeiros. A aplicação está implementada em ASP.NET Core, persiste os dados no SQL Server com Entity Framework Core e protege os recursos da API com JWT.

## O que está implementado

- login com e-mail e senha e emissão de token JWT;
- consulta dos dados do usuário autenticado;
- logout stateless (o endpoint encerra a requisição, mas não revoga o token JWT);
- listagem de usuários;
- criação, listagem, atualização e exclusão de lançamentos;
- paginação, busca e filtros na listagem de lançamentos;
- migrações do banco executadas na inicialização;
- carga inicial idempotente de um administrador e de lançamentos demonstrativos;
- documentação Swagger no ambiente `Development`;
- endpoints de health check em `/health` e `/alive`;
- execução por Docker Compose ou .NET Aspire;
- testes de integração da autenticação e dos lançamentos.

## Tecnologias presentes no projeto

- .NET 10 e ASP.NET Core;
- Entity Framework Core 10;
- SQL Server 2022;
- autenticação JWT Bearer;
- Swagger / OpenAPI;
- .NET Aspire;
- xUnit;
- Docker e Docker Compose.

## Estrutura

```text
.
├── backend/
│   ├── Almirante.Api/             # API, regras, persistência e migrações
│   ├── Almirante.Api.Tests/       # testes de integração
│   ├── Almirante.AppHost/         # orquestração local com Aspire
│   ├── Almirante.ServiceDefaults/ # health checks, telemetria e service discovery
│   └── Almirante.slnx
├── compose.yaml
├── .env.example
└── README.md
```

## Executar com Docker Compose

### Pré-requisitos

- Docker com o comando `docker compose` disponível.

### Passos

Na raiz do repositório, crie o arquivo de configuração local:

```bash
cp .env.example .env
```

Altere no `.env`, no mínimo, os valores de `SQL_SA_PASSWORD`, `JWT_KEY` e `SEED_ADMIN_SENHA`. Em seguida, inicie a API e o SQL Server:

```bash
docker compose up --build
```

Com os valores fornecidos pelo `.env.example`, os serviços ficam disponíveis em:

- API: `http://localhost:8090`;
- health check: `http://localhost:8090/health`;
- Swagger: `http://localhost:8090/swagger` porque o exemplo define `ASPNETCORE_ENVIRONMENT=Development`;
- SQL Server: `localhost,14330`.

O Compose usa o volume nomeado `almirante-sqlserver-data` para persistir os dados do SQL Server.

Para encerrar os contêineres:

```bash
docker compose down
```

Esse comando mantém o volume de dados. Para também removê-lo:

```bash
docker compose down -v
```

## Executar com .NET Aspire

### Pré-requisitos

- SDK do .NET 10;
- runtime de contêiner compatível com o Aspire, usado para iniciar o SQL Server.

Execute o AppHost:

```bash
dotnet run --project backend/Almirante.AppHost
```

O AppHost cria o recurso SQL Server com volume persistente, registra o banco `almirante` e inicia o projeto da API. Os endereços atribuídos aos recursos são exibidos pelo Aspire durante a execução.

As configurações de desenvolvimento já contêm uma chave JWT e as credenciais do administrador de seed usadas localmente:

- e-mail: `admin@local.dev`;
- senha: `senha`.

## Configuração por variáveis de ambiente

O arquivo `.env.example` é consumido pelo Docker Compose e documenta os valores aceitos:

| Variável | Uso | Valor padrão no Compose |
| --- | --- | --- |
| `SQL_SA_PASSWORD` | senha do usuário `sa` do SQL Server | obrigatória |
| `SQL_HOST_PORT` | porta do SQL Server publicada no host | `14330` |
| `API_HOST_PORT` | porta HTTP da API publicada no host | `8090` |
| `ASPNETCORE_ENVIRONMENT` | ambiente da aplicação | `Production` |
| `JWT_ISSUER` | emissor do token JWT | `Almirante.Api` |
| `JWT_AUDIENCE` | audiência do token JWT | `Almirante.Frontend` |
| `JWT_KEY` | chave usada para assinar e validar o JWT | obrigatória |
| `JWT_EXPIRATION_MINUTES` | duração do token em minutos | `60` |
| `SEED_ADMIN_NOME` | nome do administrador inicial | `Administrador` |
| `SEED_ADMIN_EMAIL` | e-mail do administrador inicial | `admin@local.dev` |
| `SEED_ADMIN_SENHA` | senha do administrador inicial | obrigatória |
| `CORS_ORIGIN_1` | primeira origem aceita pelo CORS | `http://localhost:4200` |
| `CORS_ORIGIN_2` | segunda origem aceita pelo CORS | `http://localhost:4201` |

As chaves obrigatórias são exigidas pela interpolação do `compose.yaml`. O arquivo `.env` contém segredos locais e não deve ser versionado.

## Autenticação

Faça login com o administrador criado pelo seed:

```bash
curl --request POST http://localhost:8090/api/Auth/login \
  --header 'Content-Type: application/json' \
  --data '{"email":"admin@local.dev","senha":"senha"}'
```

A resposta contém `token.accessToken`, `token.expiresAtUtc` e os dados do usuário. Envie o token nos endpoints protegidos:

```http
Authorization: Bearer SEU_TOKEN
```

## Endpoints implementados

Todos os endpoints, exceto o login e os health checks, exigem um token JWT válido.

| Método | Rota | Comportamento |
| --- | --- | --- |
| `POST` | `/api/Auth/login` | valida e-mail e senha e retorna o token e o usuário |
| `POST` | `/api/Auth/logout` | retorna `204 No Content`; não revoga o JWT |
| `GET` | `/api/Auth/Me` | retorna o usuário autenticado |
| `GET` | `/api/Usuarios` | lista os usuários por nome |
| `GET` | `/api/Lancamentos` | lista lançamentos com paginação e filtros |
| `POST` | `/api/Lancamentos` | cria um lançamento |
| `PUT` | `/api/Lancamentos/{id}` | atualiza os campos enviados de um lançamento |
| `DELETE` | `/api/Lancamentos/{id}` | exclui um lançamento |
| `GET` | `/health` | informa a prontidão da aplicação |
| `GET` | `/alive` | informa se a aplicação está ativa |

### Consulta de lançamentos

`GET /api/Lancamentos` aceita os parâmetros:

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

### Dados aceitos em lançamentos

Na criação, `membroNome`, `tipo`, `categoria`, `vencimento` e `status` são obrigatórios. `membroId`, `descricao` e `moeda` são opcionais; quando `moeda` não é informada, a API usa `BRL`. O valor não pode ser negativo.

Valores validados pela API:

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

## Banco de dados e carga inicial

Na inicialização, a API aplica as migrações do Entity Framework Core. Depois, cria o administrador configurado caso ainda não exista um usuário com o mesmo e-mail normalizado. Também inclui 20 lançamentos demonstrativos somente quando a tabela de lançamentos está vazia.

## Testes

Execute a suíte a partir da raiz do repositório:

```bash
dotnet test backend/Almirante.slnx
```

Os testes usam o provedor em memória do Entity Framework Core e cobrem autenticação, autorização, listagem, filtros, paginação, validação e o fluxo de criação, atualização e exclusão de lançamentos.
