# Desbravadores.Backend

[![Backend CI](https://github.com/LucasDevolps/Desbravadores.Backend/actions/workflows/backend-ci.yml/badge.svg)](https://github.com/LucasDevolps/Desbravadores.Backend/actions/workflows/backend-ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

Backend (API HTTP) do projeto **Almirante**, que centraliza a gestão dos Desbravadores:
autenticação e gestão de usuários, eventos com cobranças por membro e lançamentos financeiros.

- **Stack:** .NET 10 / ASP.NET Core, SQL Server com Entity Framework Core, autenticação JWT Bearer.
- **Estado:** MVP funcional, com CI no GitHub Actions — detalhes em [Estado atual](#estado-atual).
- **Arquitetura:** diagramas C4 e decisões arquiteturais (ADRs) em [`docs/architecture`](docs/architecture/README.md).

## Estado atual

O backend está em fase de **MVP funcional** e possui:

- login com e-mail e senha e emissão de token JWT;
- consulta dos dados do usuário autenticado;
- sessões persistidas, refresh rotativo e logout com revogação efetiva;
- listagem de usuários;
- criação, listagem, atualização e exclusão de lançamentos financeiros;
- registro e correção do pagamento efetivo (data e valor liquidados) de cada lançamento;
- dashboard administrativo com saldo realizado, entradas, saídas, desbravadores ativos e próximos eventos;
- notificações de débito e de evento por destinatário, com leitura individual e avisos em tempo real por SignalR;
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
- integração contínua com GitHub Actions em runners hospedados e descartáveis.
- releases SemVer a partir de tags, com a imagem da API publicada no GHCR e rollback por digest.

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
│   ├── ISSUE_TEMPLATE/             # formulários de bug, feature, segurança e dívida técnica
│   ├── CODEOWNERS
│   ├── dependabot.yml              # atualizações semanais de NuGet, Actions e imagens Docker
│   ├── release.yml                 # categorias das release notes geradas pelo GitHub
│   ├── pull_request_template.md
│   └── workflows/
│       ├── backend-ci.yml          # restore, build e testes do backend
│       ├── codeql.yml              # análise estática CodeQL (C#)
│       ├── container-security.yml  # scan da imagem da API (Trivy) e SBOM CycloneDX
│       └── release.yml             # release: tag SemVer → imagem no GHCR + GitHub Release
├── src/
│   ├── Almirante.Api/              # API, regras, persistência e migrações
│   ├── Almirante.Api.Tests/        # testes de integração
│   ├── Almirante.AppHost/          # orquestração local com Aspire
│   ├── Almirante.ServiceDefaults/  # health checks, telemetria e service discovery
│   └── Almirante.slnx
├── nginx/
│   └── nginx.conf                  # reverse proxy e rate limit do login (Docker Compose)
├── compose.yaml
├── compose.release.yaml            # overlay: API a partir da imagem de uma release (GHCR)
├── .env.example
├── CONTRIBUTING.md
├── SECURITY.md
└── README.md
```

## Executar com Docker Compose

Passo a passo completo para uma máquina Ubuntu/Pop!_OS, da instalação do Docker ao backup:
[`docs/instalacao-ubuntu.md`](docs/instalacao-ubuntu.md).

### Pré-requisitos

- Docker com o comando `docker compose` disponível.

### Configuração

Na raiz do repositório, crie o arquivo de configuração local:

```bash
cp .env.example .env
```

Altere no `.env`, no mínimo, os valores de:

- `SQL_SA_PASSWORD` (só o SQL Server e o bootstrap a conhecem; a API nunca a recebe);
- `SQL_ADMIN_USER` e `SQL_ADMIN_PASSWORD` (identidade administrativa dedicada da API, obrigatórias, nunca `sa`; 16+ caracteres, diferente da senha do `sa`);
- `JWT_KEY_V1` (Base64 de no mínimo 32 bytes) e `JWT_ACTIVE_KEY_ID`;
- `SEED_ADMIN_SENHA` (política: 12–128 caracteres, não trivial; ver [`docs/authentication-security.md`](docs/authentication-security.md)).

Todo valor `DEFINA_...` do `.env.example` é um placeholder: a API recusa iniciar com a chave JWT placeholder e recusa criar o admin inicial com senha placeholder ou fraca. Gere a chave com `openssl rand -base64 32`. O arquivo `.env` contém segredos locais e não deve ser versionado.

Antes de iniciar com HTTPS local, prepare o [certificado TLS](docs/local-login.md).

### Inicialização

```bash
docker compose -f compose.yaml -f compose.https.yaml up --build
```

Com os valores do `.env.example`, os serviços ficam disponíveis em:

- API (HTTPS, obrigatório para autenticação): `https://localhost:8443`;
- API (HTTP): `http://localhost:8090`;
- Swagger: `http://localhost:8090/swagger`;
- readiness: `http://localhost:8090/health`;
- liveness: `http://localhost:8090/alive`;
- SQL Server: `localhost,14330`.

O arquivo `compose.https.yaml` habilita o TLS local. O Compose base continua disponível sem certificados para ambientes que configuram sua própria terminação TLS.

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

### TLS num ambiente publicado (domínio/IP público real)

`compose.https.yaml` (acima) é só para desenvolvimento local, com certificado autoassinado. Para um
ambiente acessível fora da máquina local, use o overlay `compose.tls.yaml` com um certificado real
(emitido por uma CA, ex.: Let's Encrypt, ou fornecido pela infraestrutura do domínio):

```bash
# .env desse ambiente: NGINX_CONF_FILE=nginx.tls.conf, TLS_CERT_PATH, TLS_KEY_PATH, API_HOST_PORT=80
docker compose -f compose.yaml -f compose.tls.yaml up -d --build
```

`nginx/nginx.tls.conf` faz a porta 80 **somente** redirecionar (`308`) para HTTPS; o conteúdo é
servido apenas em 443, com o certificado apontado por `TLS_CERT_PATH`/`TLS_KEY_PATH` (fora do Git).
Detalhes, geração/validação do certificado e o que preencher no `.env` (inclusive para quem usa
Pop!_OS/Linux) estão em [`docs/authentication-security.md`](docs/authentication-security.md).

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
- além do Nginx, a própria API limita o login por IP (padrão 5 tentativas/60 s, `429` com `Retry-After`) e bloqueia temporariamente a conta após falhas consecutivas de senha (padrão 10 em 15 min, bloqueio de 15 min) — ver [`docs/authentication-security.md`](docs/authentication-security.md);
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
  confiança é numa subnet inteira;
- as fotos de usuários ficam numa pasta **fora** de `C:\desbravadores\site` (o `robocopy /MIR` do deploy apagaria o
  que estivesse dentro), informada ao app pool pela variável `Media__Storage__Path`. O deploy prepara isso sozinho,
  antes de tirar o site do ar: sem a variável, usa a pasta `fotos` ao lado do site e a define no pool; sem a pasta,
  cria-a com a ACL abaixo (o pool lê, grava e apaga, sem permissão de execução; herança desligada só na raiz, nunca
  com `/T`). Uma pasta já existente mantém a ACL que tiver. O equivalente manual, como administrador:

  ```powershell
  New-Item -ItemType Directory -Force C:\desbravadores\fotos | Out-Null
  icacls C:\desbravadores\fotos /inheritance:r /grant:r "IIS AppPool\Almirante:(OI)(CI)(R,W,D)" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
  Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Name '.' `
    -Filter "system.applicationHost/applicationPools/add[@name='Almirante']/environmentVariables" `
    -Value @{ name = 'Media__Storage__Path'; value = 'C:\desbravadores\fotos' }
  ```

- o hub SignalR de notificações (issue #130) precisa do recurso **WebSocket Protocol** do IIS; sem ele, o cliente
  cai para long polling. O handshake WebSocket do navegador leva o `access_token` na query, então o log W3C do site
  não pode gravar `UriQuery`. O deploy garante as duas coisas antes de tirar o site do ar (passo "Garantir WebSocket
  no IIS e log sem query string"). O equivalente manual, como administrador (depois, pare e inicie o pool):

  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart
  $filtro = "system.applicationHost/sites/site[@name='Almirante.Api']/logFile"
  $campos = "$(Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filtro -Name logExtFileFlags)"
  Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filtro -Name logExtFileFlags `
    -Value (($campos -split ',' | Where-Object { $_.Trim() -ne 'UriQuery' }) -join ',')
  ```

O `.NET Aspire` (seção abaixo) continua executando a API diretamente, sem Nginx, em desenvolvimento
local — a proteção de rate limit só se aplica aos dois caminhos de deploy (Docker Compose e IIS).

## Executar com .NET Aspire

### Pré-requisitos

- SDK do .NET 10;
- runtime de contêiner compatível com o Aspire.

Execute o AppHost:

```bash
dotnet run --project src/Almirante.AppHost
```

O AppHost:

- inicia um SQL Server em contêiner;
- utiliza o volume persistente `almirante-sqlserver-data`;
- executa o contêiner `sql-bootstrap` (o mesmo `scripts/sql-bootstrap.sh` do Compose), que cria o banco `almirante` e as identidades SQL da API (administrativa dedicada e de runtime, sem privilégio de servidor);
- aguarda o bootstrap terminar;
- inicia a API com essas identidades (o AppHost não repassa o `sa` a ela) e exibe os endereços dos recursos no painel do Aspire.

Nenhuma credencial fica versionada. Antes da primeira execução, defina os segredos locais fora do Git:

```bash
dotnet user-secrets set "Parameters:sql-password" "<senha do sa do SQL Server>" --project src/Almirante.AppHost
dotnet user-secrets set "Parameters:sql-admin-password" "<senha administrativa dedicada, 16+ caracteres>" --project src/Almirante.AppHost
dotnet user-secrets set "Jwt:ActiveKeyId" "dev" --project src/Almirante.Api
dotnet user-secrets set "Jwt:Keys:dev" "<openssl rand -base64 32>" --project src/Almirante.Api
dotnet user-secrets set "SeedAdmin:Senha" "<senha forte>" --project src/Almirante.Api
```

O e-mail do administrador inicial é `admin@local.dev`. Se o volume `almirante-sqlserver-data` já existir, use a senha do SQL Server com que ele foi criado.

## Variáveis de ambiente

O arquivo `.env.example` é consumido pelo Docker Compose e documenta as configurações disponíveis:

| Variável | Finalidade | Padrão no Compose |
| --- | --- | --- |
| `SQL_SA_PASSWORD` | senha do usuário `sa` do SQL Server; usada só pelos serviços `sqlserver` e `sql-bootstrap` — a API não a recebe e recusa iniciar se receber | obrigatória |
| `SQL_ADMIN_USER` / `SQL_ADMIN_PASSWORD` | identidade administrativa dedicada da API (usuário contido criado pelo `sql-bootstrap`: migrations, concessões e rotação da senha de runtime; sem privilégio de servidor). Sem padrão e sem fallback para `sa` | obrigatórias |
| `SQL_APP_USER` / `SQL_APP_PASSWORD_ROTATION_HOURS` | identidade de runtime da API (senha aleatória rotacionada em memória) e intervalo da rotação | `almirante_user_bd` / `24` |
| `SQL_HOST_PORT` | porta do SQL Server publicada no host | `14330` |
| `SQL_TRUST_SERVER_CERTIFICATE` | aceita o certificado autoassinado do SQL Server do container; **só para desenvolvimento** — `True` em `Production` faz a API recusar iniciar (#36) | `False` |
| `API_HOST_PORT` | porta HTTP publicada no host pelo nginx (reverse proxy da API) | `8090` |
| `API_HTTPS_HOST_PORT` | porta HTTPS publicada no host pelo nginx com `compose.https.yaml` (desenvolvimento local, certificado autoassinado) | `8443` |
| `NGINX_CONF_FILE` | arquivo em `nginx/` montado como config do nginx; `nginx.tls.conf` ativa TLS publicado (redirect 80→443) com `compose.tls.yaml` | `nginx.conf` |
| `TLS_CERT_PATH` / `TLS_KEY_PATH` | caminhos, fora do Git, do certificado/chave privada reais montados por `compose.tls.yaml` | obrigatórias só com `compose.tls.yaml` |
| `TLS_HTTPS_HOST_PORT` | porta HTTPS publicada no host pelo nginx com `compose.tls.yaml` (ambiente publicado, certificado real) | `443` |
| `API_TRUSTED_PROXY_CIDR` | rede (CIDR) confiável para os headers X-Forwarded-For/X-Forwarded-Proto enviados pelo nginx; deve corresponder à subnet de `almirante-net` no `compose.yaml` | `172.30.0.0/24` |
| `ASPNETCORE_ENVIRONMENT` | ambiente da aplicação | `Production` |
| `JWT_ISSUER` | emissor do token JWT | `Almirante.Api` |
| `JWT_AUDIENCE` | audiência do token JWT | `Almirante.Frontend` |
| `JWT_ACTIVE_KEY_ID` | `kid` usado para novas assinaturas | `v1` |
| `JWT_KEY_V1` | chave HS256 Base64 associada a `v1` | obrigatória |
| `JWT_ACCESS_TOKEN_MINUTES` | duração máxima do access token | `10` |
| `AUTH_SESSION_DAYS` | limite absoluto da sessão | `7` |
| `AUTH_REFRESH_INACTIVITY_HOURS` | inatividade máxima entre login/refresh | `24` |
| `LOGIN_RATE_LIMIT_PERMITS` / `LOGIN_RATE_LIMIT_WINDOW_SECONDS` | tentativas de login por IP na janela da API | `5` / `60` |
| `LOGIN_LOCKOUT_MAX_FAILURES` / `LOGIN_LOCKOUT_FAILURE_WINDOW_MINUTES` / `LOGIN_LOCKOUT_MINUTES` | lockout temporário por conta | `10` / `15` / `15` |
| `SEED_ADMIN_NOME` | nome do administrador inicial | `Administrador` |
| `SEED_ADMIN_EMAIL` | e-mail do administrador inicial | `admin@local.dev` |
| `SEED_ADMIN_SENHA` | senha do administrador inicial | obrigatória |
| `CORS_ORIGIN_1` | primeira origem permitida pelo CORS | `http://localhost:4200` |
| `CORS_ORIGIN_2` | segunda origem permitida pelo CORS | `http://localhost:4201` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | endpoint opcional para exportação OTLP | não definido |

A chave decodificada deve possuir pelo menos 32 bytes aleatórios e ser diferente em cada ambiente.

## Autenticação

O login exige HTTPS e o par cookie/header CSRF. Veja o [passo a passo de login local](docs/local-login.md), incluindo certificado, cookies e cURL. Com `compose.https.yaml`, a porta HTTPS vem de `API_HTTPS_HOST_PORT` (`8443` por padrão). A porta `API_HOST_PORT` atende HTTP e não deve ser usada para autenticação.

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
| `GET` | `/api/Auth/Me` | retorna o perfil do usuário autenticado (qualquer cargo) |
| `GET` | `/api/Usuarios` | lista os usuários ordenados por nome (ADM, DIR, DIRA, SEC, TES) |
| `PUT` | `/api/Usuarios/{id}/foto` | envia ou substitui a foto de perfil (multipart, campo `arquivo`) |
| `GET` | `/api/Usuarios/{id}/foto` | baixa a foto (WebP gerado pela API) |
| `DELETE` | `/api/Usuarios/{id}/foto` | remove a foto |
| `GET` | `/api/Cep/{cep}` | consulta o CEP no ViaCEP para preencher o endereço (ADM, DIR, DIRA, SEC, TES; nada é gravado) |
| `GET` | `/api/Cargos` | lista os cargos (ADM, DIR, DIRA, SEC, TES) |
| `GET` | `/health` | informa a prontidão da aplicação |
| `GET` | `/alive` | informa se a aplicação está ativa |

**Dados pessoais de usuários.** `POST`, `PUT` e `GET /api/Usuarios` trafegam `cpf`, `dataNascimento`
(`yyyy-MM-dd`, sem horário, não futura) e `telefone`, `null` em cadastros antigos. O **`cpf` é obrigatório no
cadastro** (`POST` sem CPF retorna `400`); no `PUT` ele pode ser trocado, mas um CPF já gravado não pode ser removido
(`400`), e cadastros antigos sem CPF continuam editáveis sem ele. Data de nascimento e telefone são opcionais. O
`email` de login passou a ter no máximo 100 caracteres ASCII. O CPF é aceito com ou sem máscara, precisa ter 11
dígitos com dígitos verificadores válidos e é gravado só com os dígitos; é único entre todos os usuários, inclusive
inativos (índice `UX_Usuarios_Cpf`; duplicidade gera `409`). O telefone é fixo (10 dígitos, começando de 2 a 5) ou
celular (11 dígitos, começando com 9), com DDD válido, com ou sem a máscara `(11) 98765-4321`, e é gravado só com
dígitos (valores antigos com máscara ficam como estão até serem alterados). No `PUT`, um desses campos **omitido**
mantém o valor atual, e `null` (ou `""` em `cpf`/`telefone`) o remove (exceto o CPF gravado, acima). Alteração e
exclusão lógica gravam o estado anterior completo, incluindo esses campos, em `_usuarios_hist`, pela trigger
`TR_Usuarios_Historico`, na mesma transação.

**RG, tipo sanguíneo e doenças/alergias.** Tipo sanguíneo e doenças/alergias são opcionais, e o RG depende de `ehCin` (abaixo)
(`null` quando não informados ou em cadastros antigos; nenhum valor padrão é inventado); seguem a mesma regra de omissão/limpeza
no `PUT`. `rg`: até 20 caracteres (letras sem
acento, dígitos, ponto, hífen, barra e espaço, com ao menos um dígito), gravado como informado e não único.
`tipoSanguineo`: `A+`, `A-`, `B+`, `B-`, `AB+`, `AB-`, `O+` ou `O-` (aceita minúsculas, grava em maiúsculas; o banco
também recusa outro valor pela CHECK `CK_Usuarios_TipoSanguineo`). `doencasAlergias`: **um único texto livre** (um
`textarea` no frontend, com quebras de linha) com doenças, alergias e cuidados de saúde, até 2000 caracteres na página
1252 (acentos sim, emojis não); acima do limite é recusado com `400`, nunca truncado. São dados sensíveis: saem só em
`/api/Usuarios` (mesma autorização do CRUD), nunca em `/api/Auth/Me`, e não são registrados em log.

**Documento: CIN e validade (issue #131).** `ehCin` é `true` quando o documento de identidade do usuário é a Carteira de
Identidade Nacional; omitido no `POST` grava `false`, e cadastros antigos ficam `false`. `dataExpiracao` é a data de
validade desse documento (`yyyy-MM-dd`, opcional, `null` em cadastros antigos, pode estar no passado); nada bloqueia login
por validade nem a liga a `ehCin`. No `PUT`, omitidos mantêm o valor gravado; `ehCin: false` é gravado (não é omissão) e
`ehCin: null` é recusado com `400`; `dataExpiracao: null` remove a data.

`ehCin` decide se o **RG é obrigatório**: a CIN usa o CPF como número, então com `ehCin: true` só o CPF é exigido e o `rg` é
opcional (pode ser informado e continua gravado); com `ehCin: false` (ou omitido no `POST`) o `rg` é obrigatório (`400` em
`errors["Rg"]`). No `PUT` a regra usa os valores efetivos (corpo ou gravados) e segue o padrão do CPF: com `ehCin` false, um RG
gravado não pode ser removido, sair da CIN exige RG e quem informa `rg` ou `ehCin` no corpo precisa ter RG; um cadastro antigo
sem RG e sem CIN que não envia esses campos continua editável.

**Endereço (issue #131).** O cliente envia `endereco: { cep, numero, complemento }` e a API consulta o
[ViaCEP](https://viacep.com.br) e grava cada campo em sua coluna (`Usuarios.Endereco*`, `varchar`): `cep` (8 dígitos, sem
hífen, zeros à esquerda preservados), `logradouro`, `complementoCep` (o complemento do próprio CEP, ex.: "lado ímpar"),
`unidade`, `bairro`, `localidade`, `uf`, `estado`, `regiao`, `ibge`, `gia`, `ddd` e `siafi`, junto de `numero` (até 20) e
`complemento` (o do usuário, até 100). Campos que o ViaCEP devolve vazios ficam `null`; nenhum valor é inventado. O CEP é
aceito com ou sem hífen (`04382-160` ou `04382160`); outro formato é `400` sem chamar o ViaCEP. A resposta de
`/api/Usuarios` traz o objeto `endereco` (`null` sem endereço) e, como os dados de saúde, ele não sai em `/api/Auth/Me` nem
é registrado em log (o cliente do ViaCEP não usa o log padrão do `HttpClient`, que gravaria a URL com o CEP). Para preencher o formulário antes de salvar, use `GET /api/Cep/{cep}` (mesmos campos do
ViaCEP, com `complemento` = complemento do CEP).

No `PUT`, `endereco` omitido mantém tudo; `null` (ou `cep` `null`/`""`) remove o endereço inteiro; dentro do objeto, `cep`,
`numero` e `complemento` omitidos mantêm o valor gravado. Só um CEP **diferente** do gravado é consultado; aí todos os dados
do CEP são substituídos e `numero`/`complemento` omitidos ficam vazios (eram do endereço anterior). CEP inexistente retorna
`400` (`errors["Endereco.Cep"]`); ViaCEP fora do ar, lento ou com resposta fora do contrato (CEP trocado, campo longo
demais, caracteres fora da página 1252, código não numérico) retorna `503` — em todos esses casos **nada é gravado** e o
endereço anterior fica intacto. A consulta do `PUT` é feita antes da transação, sem segurar o lock da linha. A CHECK
`CK_Usuarios_Endereco` garante no banco que sem CEP não há nenhum campo de endereço e que, com CEP, há 8 dígitos, cidade e UF.
`ehCin`, `dataExpiracao` e todos os campos do endereço entram no estado anterior gravado em `_usuarios_hist` pela trigger
`TR_Usuarios_Historico`, inclusive quando só eles mudam (`ehCin` fica `null` nas linhas de histórico anteriores à migration
`AddUsuariosCinExpiracaoEndereco`).

| Configuração (`ViaCep`) | Padrão | O que controla |
| --- | --- | --- |
| `BaseUrl` | `https://viacep.com.br/ws/` | endereço do serviço (só `https`, terminado em `/`) |
| `TimeoutMs` | `5000` | prazo total de uma consulta (200 a 30000), com uma nova tentativa rápida em falha transitória (5xx, 408, 429, rede) |

**Responsável (issues #112 e #131).** `responsavelNome`, `responsavelEmail`, `responsavelCpf` e `responsavelTelefone`
seguem as mesmas regras dos campos equivalentes do usuário (o CPF do responsável não é único e não pode ser igual ao
do próprio usuário; o nome aceita acentos, mas não emojis nem outros alfabetos, porque a coluna é `varchar`). Nome e
e-mail do responsável são **obrigatórios quando o usuário tem menos de 18 anos** na data de hoje (fuso
`Clube:FusoHorario`, padrão `America/Sao_Paulo`); para maior de idade, ou sem data de nascimento, são opcionais. A
idade é contada no calendário, dia a dia: a pessoa é menor até a véspera do 18º aniversário, e quem nasceu em 29/02
completa 18 anos em 01/03 (Código Civil, art. 132, §3º). No `PUT`, a regra usa a data de nascimento e os dados do
responsável **efetivos**: os do corpo ou, quando omitidos, os já gravados.

**Foto de perfil (issue #116).** `PUT /api/Usuarios/{id}/foto` recebe `multipart/form-data` com um único campo
`arquivo` (JPEG, PNG ou WebP, com extensão, `Content-Type` e assinatura coincidentes). A API decodifica a imagem,
aplica a orientação do EXIF, remove todos os metadados (EXIF com GPS, XMP, IPTC, perfil de cor) e regrava em WebP de no
máximo 1024x1024; o original nunca é guardado. O banco grava só o caminho relativo (`Usuarios.FotoCaminho`) e o arquivo
fica na pasta `Media:Storage:Path`, fora do site. A troca apaga a foto anterior só depois de gravar a nova, e a troca ou
remoção vai para `_usuarios_hist`. A listagem traz `fotoUrl` (`null` sem foto). Mesma autorização do CRUD de usuários
(só um Administrador gerencia a foto de um Administrador); o envio exige usuário ativo, a remoção vale também para
inativo. Respostas: `400` multipart inválido, `404`, `409` usuário inativo, `413` acima do limite, `415` formato ou
`Content-Encoding` não suportado, `422` imagem inválida ou fora dos limites, `429` excesso de envios (com
`Retry-After`), `503` armazenamento indisponível. Decisão e alternativas: [ADR-0011](docs/architecture/adr/0011-armazenar-fotos-em-disco-fora-do-site-e-servir-pela-api.md).

No Angular, a foto não pode ser exibida com uma `<img src>` comum, porque ela não envia o token: busque com
`HttpClient` (`responseType: 'blob'`) e use `URL.createObjectURL`. A CSP do front precisa aceitar `img-src blob:`. A
resposta tem `ETag` (muda a cada troca) e `Cache-Control: private, no-cache`.

| Configuração (`Media:Images`) | Padrão | O que limita |
| --- | --- | --- |
| `MaxUploadSizeMb` | `10` | tamanho do arquivo enviado (o nginx aceita 11 MB só nessa rota) |
| `MinWidth` / `MinHeight` | `64` | dimensões mínimas da imagem enviada |
| `MaxMegapixels` | `25` | pixels da imagem enviada, conferidos no cabeçalho antes de decodificar |
| `MaxWidth` / `MaxHeight` | `1024` | dimensões da foto gravada (nunca amplia) |
| `WebpQuality` / `MinWebpQuality` | `82` / `60` | qualidade inicial e mínima do WebP gravado |
| `MaxStoredSizeMb` | `1` | tamanho da foto gravada (a qualidade baixa até caber) |
| `ProcessingTimeoutMs` | `10000` | prazo do processamento de uma imagem |
| `MaxConcurrentProcessing` | `2` | imagens processadas ao mesmo tempo (fila curta, depois `429`) |
| `UploadsPerUserPerWindow` / `UploadWindowMinutes` | `10` / `10` | envios por usuário na janela |

`Media:Storage:Path` é obrigatório fora de Development (em Development, sem valor, usa uma pasta descartável no
temp) e a API não sobe se ele for relativo, estiver dentro do site ou não aceitar escrita. No Docker Compose ele aponta
para o volume nomeado `almirante-fotos`, ou para a pasta do host em `FOTOS_HOST_PATH`, quando definida. O deploy Linux
define `$HOME/almirante/fotos` (ao lado do `.env` fixo) e cria a pasta com dono `app` e modo `700`. Na primeira vez,
copia as fotos que já estivessem no volume, que é mantido. Para ver ou copiar os arquivos, use `sudo` ou um container.
No IIS, veja [Docker/Nginx no Windows (IIS)](#dockernginx-no-windows-iis).
Uma faxina diária remove gravações interrompidas e fotos sem referência no banco
(`Media:Storage:LimpezaHabilitada`).

## Lançamentos

O agregado financeiro possui um único fluxo de criação, protegido pelas roles `ADM`, `DIR`,
`DIRA`, `SEC` e `TES`. Sem autenticação válida a resposta é `401`; autenticado com outro cargo, `403`.

| Método | Rota | Finalidade |
|---|---|---|
| `GET` | `/api/Lancamentos` | Lista somente lançamentos ativos, com paginação e filtros |
| `GET` | `/api/Lancamentos/{id}` | Consulta um lançamento ativo; destino do `Location` da criação individual |
| `POST` | `/api/Lancamentos/Registrar` | Registra para um membro ou, atomicamente, para todos |
| `PUT` | `/api/Lancamentos/{id}` | Atualiza campos permitidos (não marca como `Pago`) |
| `POST` | `/api/Lancamentos/{id}/Pagamento` | Registra o pagamento efetivo (data e valor liquidados) |
| `PUT` | `/api/Lancamentos/{id}/Pagamento` | Corrige data e/ou valor de um pagamento já registrado |
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

No modo geral (`aplicarATodosOsMembros: true`), `Idempotency-Key` é obrigatório e limitado a 100 caracteres. Uma única
operação carrega os IDs elegíveis, insere o lote com `AddRange`/`SaveChanges` atômico e registra
`OperacaoId`. Repetir chave e payload devolve a operação existente; mudar o payload resulta em
`409 Conflict`. O hash inclui finalidade, descrição, categoria, fluxo, valor e vencimento.

No modo individual, `Idempotency-Key` é opcional (issue #130). Sem a chave, nada muda: cada `POST`
é uma nova cobrança, mesmo com conteúdo igual. Com a chave, reenviar a mesma chave e os mesmos dados
devolve `200` com o lançamento já criado, sem duplicar o lançamento nem a notificação. A mesma chave
com outro conteúdo, outro membro ou usada no outro modo devolve `409`. A operação fica em
`LancamentosOperacoes`, com `OperacaoId` no lançamento.

`DELETE` recebe `{ "motivo": "..." }`, apenas muda `Ativo` para `false` e mantém a auditoria por
trigger/`SESSION_CONTEXT`. Listagens projetam o nome com JOIN, sem armazená-lo em `Lancamentos` e
sem N+1. `Finalidade` (Mensalidade, Campori etc.) é a natureza; `TipoFluxo` (Entrada/Saida) é a
direção financeira, portanto ambos permanecem.

Categoria, status e fluxo são enums com valores explícitos e `Description`:

| Campo | Valores |
|---|---|
| `CategoriaLancamento` | `0 = Evento`, `1 = Clube` |
| `StatusLancamento` | `0 = Pendente`, `1 = Pago`, `2 = Atrasado` |
| `TipoFluxoLancamento` | `0 = Entrada`, `1 = Saida` (descrição: “Saída”) |

O JSON continua retornando nomes textuais; criação e atualização aceitam tanto nomes quanto
os códigos numéricos correspondentes e rejeitam valores fora do enum. `status` na listagem
aceita nome, código ou `Todos`. Clientes que enviavam `tipoFluxo: "Despesa"` devem passar a
usar `"Saida"` ou `1`. A descrição com acento é metadado do enum; o nome no JSON é `Saida`.

`LancamentosService` coordena consultas e alterações individuais. `LancamentoGeralService`
cuida da criação atômica em lote/idempotência; `LancamentoExclusaoService`, da transação e
contexto SQL de auditoria. `LancamentoMapping` centraliza a projeção das respostas.

### Pagamentos (issue #129)

Um lançamento só representa dinheiro efetivamente movimentado depois que o pagamento é
**registrado** com a data e o valor reais. `Valor` continua sendo o valor previsto (original).

| Campo da resposta | Significado |
|---|---|
| `dataPagamento` | Data efetiva da liquidação (`yyyy-MM-dd`); `null` sem pagamento registrado |
| `valorPago` | Valor efetivamente pago/recebido; `null` sem pagamento registrado; nunca presumido a partir de `valor` |
| `valorRestante` | Calculado, não persistido: `valor − valorPago` com pagamento; `valor` sem pagamento; `null` em "Pago" histórico sem dados |
| `pagamentoRegistradoPorUsuarioId` / `pagamentoRegistradoEmUtc` | Quem registrou ou corrigiu por último o pagamento, e quando (UTC) |

`POST /api/Lancamentos/{id}/Pagamento` registra e `PUT /api/Lancamentos/{id}/Pagamento` corrige,
ambos com o corpo:

```json
{ "dataPagamento": "2026-10-15", "valorPago": 50.00 }
```

Regras:

- `dataPagamento` é obrigatória e não pode ser futura (hoje no fuso do clube, `Clube:FusoHorario`);
  pode ser diferente do vencimento.
- `valorPago` é obrigatório, maior que zero, com até 2 casas decimais e **no máximo** o `valor` do
  lançamento. Um valor menor quita o lançamento com abatimento (`valorRestante` mostra a diferença);
  **não há pagamento parcial** com saldo em aberto.
- O registro marca o lançamento como `Pago` e grava o responsável a partir do token (nunca do corpo),
  além de `DataAtualizacao`/`AtualizadoPorUsuarioId`.
- `POST` aceita lançamentos `Pendente`, `Atrasado` ou "Pago" histórico sem dados (regularização).
  Repetir o `POST` depois de registrado retorna `409` (use o `PUT`); `PUT` sem pagamento registrado
  também retorna `409`.
- `PUT /api/Lancamentos/{id}` não marca mais como `Pago` (`409` orientando o endpoint dedicado). Em
  lançamento com pagamento registrado, ele não altera `status`, `valor` nem `tipoFluxo` (`409`);
  descrição, finalidade, categoria e vencimento continuam editáveis. Estorno (desfazer um pagamento)
  não existe nesta versão; a exclusão lógica continua disponível e copia os dados de pagamento para
  `lancamentos_deletados`.
- Concorrência: `Valor`, `Status`, `DataPagamento` e `ValorPago` são tokens de concorrência do EF;
  duas confirmações simultâneas resultam em um `200` e `409` para as demais, nunca em pagamento
  duplicado. Lançamentos de evento seguem a mesma ordem de locks de `/api/Eventos` (linha do
  evento antes do lançamento), com retentativa segura.
- O banco garante a coerência com `CK_Lancamentos_Pagamento`: ou nenhum campo de pagamento, ou
  status `Pago` com data, `0 < ValorPago <= Valor` e responsável.
- Respostas: `200` com o lançamento, `400` validação, `401`/`403` autorização, `404` inexistente ou
  excluído, `409` conflito.

**Lançamentos antigos.** A migration `AddLancamentosPagamento` não inventa datas nem valores: os
lançamentos já marcados como `Pago` ficam com `dataPagamento`/`valorPago` nulos, continuam `Pago` e
**não entram no dashboard** até serem regularizados com `POST /api/Lancamentos/{id}/Pagamento`
informando a data e o valor reais. `DataAtualizacao` nunca é usada como data de pagamento.

## Eventos

`/api/Eventos` cadastra **grupos de cobrança** de passeios/eventos. Cada `POST` cria um cadastro
(uma composição de custos + uma lista de membros) e gera **um lançamento pendente por membro**,
reutilizando as regras financeiras internamente (nenhuma chamada HTTP a `/Lancamentos`). Mesma
política dos lançamentos (`Policies.GestaoFinanceira`: `ADM`, `DIR`, `DIRA`, `SEC`, `TES`; `401`
sem autenticação, `403` sem permissão). Contrato completo, exemplos e códigos no Swagger.

| Método | Rota | Resultado |
|---|---|---|
| `GET` | `/api/Eventos[?dataInicial=yyyy-MM-dd&dataFinal=yyyy-MM-dd]` | `200` `{ items, dataInicial, dataFinal, dataMinimaCadastro }` |
| `GET` | `/api/Eventos/{id}` | `200` / `404` (destino do `Location`) |
| `POST` | `/api/Eventos` (header `Idempotency-Key: <UUID>`) | `201` criado, `200` reenvio idempotente |
| `PUT` | `/api/Eventos/{id}` | `200` com cadastro e lançamentos atualizados |
| `DELETE` | `/api/Eventos/{id}` | `204` após exclusão lógica auditada |

**Campo `membros` = `GUID | GUID[]`.** Um único GUID (`"membros": "1111…"`) e um array
(`"membros": ["1111…", "2222…"]`) são a mesma operação: o `MembrosJsonConverter` normaliza para
`IReadOnlyList<Guid>` na desserialização e toda validação/regra/hash acontece depois disso, no
POST e no PUT (um único conversor). São recusados (400): `null`, `[]`, GUID vazio/inválido, tipos
JSON diferentes, repetidos (o erro cita os IDs; nada é removido em silêncio) e IDs inexistentes.
No OpenAPI o schema é `oneOf [string/uuid, array de uuid]`, com exemplos das duas formas. A
resposta devolve sempre um array.

**Cálculo** (`decimal` / `decimal(18,2)`, ≥ 0, no máximo 2 casas):

```text
valorPorMembro = (transporte.ehGratis ? 0 : transporte.valor)
               + (alimentacao.individual ? 0 : alimentacao.valor) + seguroObrigatorio
total          = valorPorMembro × quantidadeMembros        # nunca gravado no lançamento individual
```

Os cinco campos (`ehGratis`, `individual` e os três valores) são obrigatórios no corpo — zero/`false`
são válidos; valor ignorado por um booleano é normalizado para zero antes das validações. Todos
zerados gera lançamentos pendentes de R$ 0,00. O lançamento nasce com `Finalidade = NULL` real,
`Categoria = Evento`, `Status = Pendente`, `TipoFluxo = Entrada`, `Vencimento = dataEvento`, `Ativo`
e `EventoId`; nada disso (nem responsável, IP, total) vem do cliente.

**Datas.** `dataEvento` mínima = primeiro dia do **mês atual na referência UTC** (`TimeProvider`,
independente do fuso da máquina) — não "hoje". O `GET` sem filtro usa
`[1º dia do mês − 30 dias, último dia do mês + 30 dias]` (setembro/2026: 02/08 a 30/10); com filtro
exige as duas datas (`dataInicial <= dataFinal`) e aceita períodos históricos de **até 366 dias** (a listagem não é
paginada; período maior devolve `400`). Lista vazia é `items: []`.

**Idempotência.** Chave UUID + usuário autenticado (tabela `eventos_operacoes`, separada de
`LancamentosOperacoes`). O hash usa apenas dados normalizados: data, local (trim + espaços
colapsados), booleanos, valores efetivos, seguro, `eventoReferenciaId` e membros **ordenados**;
GUID único ≡ array de um elemento, e a ordem dos membros não importa. Mesma chave + mesma operação →
`200` com o **resultado original do POST** (o mesmo DTO, com a **versão original**) — não o estado atual: se o
cadastro foi editado ou pago depois, o `GET` mostra o estado atual e o replay continua devolvendo o original
(uma edição feita a partir dele leva `409`, pois a versão original ficou antiga). O DTO original é gravado em
`eventos_operacoes.RespostaJson` na **mesma transação** do evento, dos participantes e dos lançamentos (dois
`SaveChanges`, um commit; a rowversion só existe depois do INSERT). O replay é resolvido **antes** da regra
"data a partir do 1º dia do mês", que vale só para operações novas (reenviar em outubro um POST válido de
setembro dá `200`; chave nova com a mesma data antiga dá `400`). Dados diferentes → `409` (inclusive depois
da virada do mês); chave ausente/inválida → `400`; operação cujo evento foi excluído → `409` (nunca reativa).
Falha em qualquer etapa desfaz tudo (mesma transação), então o retry não duplica cobrança.

**Operações anteriores à resposta original.** Chaves registradas antes da coluna `RespostaJson` não têm fonte
confiável do resultado original (o estado atual pode já ter mudado), então nada é inventado: o replay delas
devolve `409` com `codigo = EVENTO_IDEMPOTENCIA_SEM_RESPOSTA_ORIGINAL` e `eventoId`, orientando a consultar
o evento existente (`GET /api/Eventos/{id}`); a chave não é apagada e nada é recriado.

**Mesmo passeio, preços diferentes.** `eventoReferenciaId` (opcional) aponta para um cadastro ativo;
o servidor mantém o `eventoGrupoId`, exige mesma data e local (`400` por campo) e responde `404` se
a referência não existe/está inativa. Um membro só pode estar em **um** cadastro ativo por
`eventoGrupoId` (`409` com os IDs; garantido também no banco por índice único filtrado
`UX_evento_membros_grupo_membro_ativo`). Exemplo Ibirapuera: 8 × R$ 20 + 2 × R$ 5 = R$ 170.

**Concorrência.** `versao` (base64 do `rowversion`) vem em toda resposta e é exigida no PUT/DELETE
(`409` se desatualizada). A **mudança de status de um lançamento do evento também altera a versão**,
então um PUT/DELETE feito com versão anterior ao pagamento é recusado. Ordem única de locks, sem deadlock:
(1) coordenador do passeio — `sp_getapplock` exclusivo por `eventoGrupoId`, preso à transação, com espera
limitada (`409` ao esgotar) — em POST com `eventoReferenciaId`, PUT e DELETE; (2) linha do cadastro (`UPDATE`
com a `versao`); (3) participações e lançamentos. O pagamento usa só (2) e (3). Cada tentativa do retry do
pagamento recarrega o estado sob o lock e, se o commit falhar de forma ambígua, verifica no banco se ele
foi efetivado antes de responder sucesso.

**PUT.** Campos editáveis = os do POST + `versao` (+ `motivo`); não altera `id`, `eventoGrupoId`,
`eventoReferenciaId`, total, valor por membro nem status. Com tudo pendente: mantidos são atualizados,
novos ganham lançamento pendente e removidos são desativados (participação + lançamento, com auditoria
do trigger de lançamentos) — `motivo` (1–255) é obrigatório quando há remoção. Com algum lançamento
`Pago`/`Atrasado`: `409` para qualquer mudança na **composição** de custos (transporte, `ehGratis`, alimentação,
`individual`, seguro — não só o total), participantes ou data (pagamentos nunca voltam a pendente nem
geram estorno); só o **local** pode ser corrigido (a descrição de todos os lançamentos, inclusive `Pago`/`Atrasado`, acompanha o novo
local; valor, vencimento e status do pagamento não mudam). Data/local não mudam por PUT isolado quando o passeio
tem mais de um cadastro ativo (`409`). A data só é validada contra o mês atual se for alterada.

**DELETE** `{ "motivo": "...", "versao": "..." }`: exclusão lógica (`Ativo = false`) do cadastro, das
participações e dos lançamentos pendentes, na mesma transação; outros grupos do passeio permanecem.
`Pago`/`Atrasado` → `409` sem alterar nada; inexistente/já excluído → `404`.

**Auditoria (`historico_eventos`).** Trigger `TR_eventos_AuditoriaExclusaoLogica` (`AFTER UPDATE`, só na
transição `Ativo 1 → 0`, várias linhas) grava snapshot completo (IDs, grupo/referência, data, local,
booleanos, valores, valor por membro, total, participantes e lançamentos em JSON), responsável, IP,
motivo e horário UTC do banco. A aplicação **nunca** insere nessa tabela (o usuário de runtime tem
`DENY` de escrita, como em `lancamentos_deletados`) e a trigger falha (`THROW 50011`) sem
`SESSION_CONTEXT`. Responsável = claims validadas; IP = `HttpContext.Connection.RemoteIpAddress`
(depois do middleware de proxies confiáveis; `X-Forwarded-For` nunca é lido à mão). O contexto é
limpo no `finally` na mesma conexão física e, se a limpeza falhar, o pool dessa conexão é descartado.

**Lançamentos vinculados a evento** (`EventoId != null`): `PUT /api/Lancamentos/{id}` só aceita mudar
`status` (exceto para `Pago`) e `descricao` — valor, vencimento, finalidade, categoria e fluxo, bem como o `DELETE`, retornam
`409` orientando o uso de `/api/Eventos`. O pagamento é registrado por `POST /api/Lancamentos/{id}/Pagamento`,
que também atualiza a linha do evento (muda a `versao`). As exceções de eventos (finalidade nula, valor zero, data
passada no mês) **não** se aplicam a `POST /api/Lancamentos/Registrar`. `Lancamento.Finalidade` e
`lancamentos_deletados.Finalidade` agora aceitam `NULL`; ambos ganharam `EventoId`.

**Migration `AddEventos`** (incremental, sem reescrever migrations aplicadas): cria `eventos`,
`evento_membros`, `eventos_operacoes` e `historico_eventos`, índices (`Ativo+DataEvento`,
`EventoGrupoId`, `EventoReferenciaId`, `Lancamentos.EventoId`, participantes, índice único filtrado),
constraints (`CK_eventos_*`: não negativos, valores normalizados e soma exata, grupo coerente; FK
composta `evento_membros(EventoId, EventoGrupoId)`), recria `TR_Lancamentos_AuditoriaExclusaoLogica`
(copia `EventoId`, aceita `Finalidade` nula) e cria o trigger de eventos. `Down` é reversível
(lançamentos de evento voltam com `Finalidade = ''`). Aplicação pelo startup como as demais; nenhuma
variável de ambiente, porta ou credencial nova.

**Frontend (issue #57).** Use `GET /api/Usuarios` para os IDs; envie `membros` como GUID ou array;
guarde `versao`; gere um `Idempotency-Key` novo por tentativa de cadastro e reutilize-o apenas em
retentativas da mesma tentativa; aplique `dataMinimaCadastro` no seletor de data do cadastro.

## Dashboard

`GET /api/Dashboard[?dataInicial=yyyy-MM-dd&dataFinal=yyyy-MM-dd]` devolve, numa resposta só, os
indicadores do clube. Mesma autorização dos lançamentos (policy `GestaoFinanceira`: `ADM`, `DIR`,
`DIRA`, `SEC`, `TES`): sem token `401`, outro cargo `403`, filtros inválidos `400`.

```json
{
  "periodo": { "dataInicial": "2026-10-01", "dataFinal": "2026-10-31" },
  "financeiro": { "saldo": 1500.00, "entradas": 2500.00, "saidas": 1000.00 },
  "desbravadores": { "quantidadeAtivos": 45 },
  "proximosEventos": [
    {
      "eventoGrupoId": "6f0c…",
      "eventoIds": ["6f0c…", "a91d…"],
      "data": "2026-11-10",
      "local": "Acampamento",
      "quantidadeParticipantes": 30
    }
  ]
}
```

**Período.** Sem parâmetros: o mês atual no fuso do clube (`Clube:FusoHorario`, a mesma referência
de `DataCivil`). Com filtro, `dataInicial` e `dataFinal` são obrigatórias juntas, estritamente no
formato `yyyy-MM-dd`, inclusivas, com `dataInicial <= dataFinal` e no máximo 366 dias (mesmo limite
de `/api/Eventos`). O período afeta **somente** o financeiro.

**Financeiro.** Soma, no banco, o `valorPago` dos lançamentos ativos com status `Pago` e
`dataPagamento` dentro do período: `entradas` (`TipoFluxo = Entrada`), `saidas` (`Saida`) e
`saldo = entradas − saidas`. Não entram pendentes, atrasados, "Pago" históricos sem dados de
pagamento, lançamentos excluídos logicamente nem pagamentos fora do período. O valor previsto
(`valor`) nunca é somado. Valores em `decimal(18,2)`, sem arredondamento. Índice filtrado
`IX_Lancamentos_DataPagamento` atende a consulta.

**Desbravadores.** Total atual de usuários **ativos** com o cargo Desbravador (`Cargos.Role = 'DS'`).
Diretoria (`ADM`, `DIR`, `DIRA`, `SEC`, `TES`), demais cargos (conselheiros, instrutores, cargos de
unidade etc.) e usuários inativos não contam. Não depende do período.

**Próximos eventos.** Até 5 passeios com data igual ou posterior a hoje (fuso do clube), em ordem de
data. Os cadastros de um mesmo passeio (`EventoGrupoId`, criados com `eventoReferenciaId`) aparecem
uma única vez, com `eventoIds` dos cadastros ativos e `quantidadeParticipantes` somando os
participantes ativos de todos eles. Cadastros excluídos logicamente e eventos passados não aparecem.
Não depende do período.

## Notificações (issue #130)

Cada usuário autenticado (inclusive desbravadores) consulta e marca como lidas **as próprias**
notificações. A leitura é individual e fica gravada no banco. Avisos em tempo real chegam pelo hub
SignalR `/hubs/notificacoes`. O contrato completo para o frontend (rotas, payloads, sequência de
conexão e reconexão, deduplicação) está em [`docs/notificacoes.md`](docs/notificacoes.md).

| Método | Rota | Resultado |
|---|---|---|
| `GET` | `/api/Notificacoes/Pendentes?pageSize=20&cursor=...` | `200` `{ items, naoLidas, pageSize, proximoCursor }`, só não lidas, da mais recente para a mais antiga |
| `POST` | `/api/Notificacoes/{id}/Leitura` | `200` `{ notificacao, naoLidas }`, idempotente (preserva a 1ª data); `404` se não for do usuário |

Gatilhos desta versão, gravados na mesma transação do cadastro:

- débito individual (`POST /api/Lancamentos/Registrar`, `Entrada`) notifica o membro do lançamento;
- débito geral (`aplicarATodosOsMembros`, `Entrada`) notifica cada usuário que recebeu um lançamento,
  cada um com a própria notificação;
- novo cadastro de evento (`POST /api/Eventos`) notifica cada participante uma vez, com o valor do
  lançamento do evento (sem uma segunda notificação de débito).

`Saida` não é cobrança ao membro e não notifica. Reenvios idempotentes e concorrência não duplicam
notificações (índice único `UX_notificacoes_destinatario_origem`). O aviso em tempo real é gravado na
própria linha da notificação (outbox) e emitido depois do commit pelo `PublicadorAvisosNotificacoes`,
com nova tentativa e retomada após reinício. A API REST é a fonte da verdade. Limitações (uma instância,
IIS) e o runtime mínimo (ASP.NET Core 10.0.13, garantido pelo `Dockerfile` e pelo deploy Windows) estão
no documento.

## Banco de dados e migrations

Migrations são aplicadas incrementalmente no startup.

`AddLancamentosPagamento` (issue #129) adiciona a `Lancamentos` e `lancamentos_deletados` as colunas
nulas `DataPagamento` (`date`), `ValorPago` (`decimal(18,2)`), `PagamentoRegistradoPorUsuarioId`
(FK restritiva para `Usuarios` em `Lancamentos`) e `PagamentoRegistradoEmUtc`, cria
`CK_Lancamentos_Pagamento` e o índice filtrado `IX_Lancamentos_DataPagamento` e recria
`TR_Lancamentos_AuditoriaExclusaoLogica` para copiar os novos campos. Nenhuma linha existente é
alterada; `Down` remove as colunas e restaura o trigger anterior. Nenhuma variável de ambiente nova. `UnifyLancamentosFlow` renomeia `Tipo` para
`Finalidade`, remove `MembroNome`/`Moeda`, cria a FK restritiva para `Usuarios`, atualiza índices e
o trigger de auditoria sem apagar o histórico de migrations.

`ConvertLancamentoEnums` converte as colunas nas tabelas `Lancamentos`, `LancamentosOperacoes`
e `lancamentos_deletados` para `int`, sem recriar tabelas nem apagar registros. Também cria
constraints para limitar os códigos aceitos. Dados legados fora das categorias/status/fluxos
conhecidos interrompem a migração transacionalmente para que sejam corrigidos antes de tentar
novamente. O rollback restaura os textos anteriores, inclusive `Despesa`.

O hash histórico da idempotência é mantido: uma operação antiga de saída continua sendo
reconhecida mesmo após a mudança de nome de `Despesa` para `Saida`. Não executar versões
antiga e nova da API simultaneamente contra o schema convertido.

## Testes

```bash
dotnet test src/Almirante.slnx
```

A suíte cobre contrato e validação, membro inexistente, criação individual e geral, idempotência,
conflito de chave, soft delete, contrato de login/`/Me`, validação do JWT (assinatura, emissor,
audiência, algoritmo, expiração, tamanho), matriz RBAC (401/403/sucesso por cargo), rate limiting e
lockout, forwarded headers, segredos/política de senha, cabeçalhos de segurança e descoberta das
migrations.

Os testes de eventos (`EventosRegrasTests`, `EventosApiTests`) e `ModelBindingErrorsTests` (sanitização dos erros `400` de
model binding, sem nomes de tipos CLR) rodam sem banco; `EventosSqlServerTests`,
`EventosMigrationTests` e `ApiProcessEventosTests` (`Category=RequiresSqlServer`) exigem `ALMIRANTE_TEST_SQLSERVER`
e provam transação/rollback, `rowversion`, índice único filtrado, triggers, `SESSION_CONTEXT`, concorrência,
Up/Down da migration e o fluxo no processo real da API com a identidade de runtime de menor privilégio.
A corrida entre mudança de status de um lançamento e o PUT que remove o participante também é coberta: o status nunca
é gravado em lançamento já desativado (o `Ativo` é relido do banco depois do lock do evento).

Sem `ALMIRANTE_TEST_SQLSERVER`, todos os testes `RequiresSqlServer` falham de propósito com `Defina ALMIRANTE_TEST_SQLSERVER`
(no `dotnet test` puro isso aparece como ~145 falhas). Para rodar localmente sem tocar na instância do Windows, use um SQL Server
descartável em contêiner e a conexão `sa` no formato do CI:

```bash
ALMIRANTE_TEST_SQLSERVER="Server=127.0.0.1,<porta>;User ID=sa;Password=<senha>;Encrypt=True;TrustServerCertificate=True" dotnet test src/Almirante.slnx
```

Testes marcados `Category=RequiresSqlServer` rodam contra um SQL Server real (migrations completas,
lockout concorrente, corrida login/reset, modelo de identidades SQL sem `sa`, rotação de senha com pools,
privilégios e auditoria por trigger, startup real da API). Cada teste cria e remove bancos exclusivos e os
usuários/logins de teste. A conexão de `ALMIRANTE_TEST_SQLSERVER` é do *harness* (cria e remove o ambiente, como o
bootstrap): use uma instância descartável dedicada a testes, com sysadmin (o CI usa um contêiner com `sa` descartável).
A API sob teste nunca usa essa conexão. Exemplo com Windows Auth:

```bash
ALMIRANTE_TEST_SQLSERVER="Server=localhost;Trusted_Connection=True;TrustServerCertificate=True" dotnet test src/Almirante.slnx --filter Category=RequiresSqlServer
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

O workflow `.github/workflows/backend-ci.yml` roda em pushes e PRs para `main` e `develop`,
sem filtro de caminhos. Todos os jobs usam runners descartáveis hospedados pelo GitHub:

- `build-and-test`: instala .NET 10, compila a solução no Windows e executa os testes sem
  dependências externas.
- `sqlserver-integration`: executa **todos** os testes `Category=RequiresSqlServer` em Linux,
  com SQL Server 2022 descartável, senha aleatória, porta em loopback e nenhum volume de deploy.
- `deploy-scripts`: testa o preflight, a política dos workflows e nginx real (HTTP/HTTPS/429),
  com certificados de teste e containers descartáveis.
- `coverage`: consolida a cobertura (Coverlet) das duas suítes de teste com o ReportGenerator, mostra
  linhas e branches no Job Summary, publica o relatório HTML/Cobertura (`coverage-<sha>`) e aplica um
  quality gate contra regressão em relação ao baseline medido. Detalhes, baseline e política em
  [docs/test-coverage.md](docs/test-coverage.md).

Os testes .NET geram artefatos TRX (`unit-<sha>` e `sqlserver-<sha>`, retidos por 14 dias).
Em PRs o checkout usa o SHA do HEAD em revisão: resultado de outro commit não substitui o atual.

O workflow `.github/workflows/backend-deploy.yml` cuida da publicação em si, em runners self-hosted, a cada push em `develop`: builda e sobe os containers via Docker Compose no(s) Pop!_OS registrado(s) e publica a aplicação no IIS na máquina Windows. Em ambos os casos, as migrations pendentes rodam automaticamente na inicialização da aplicação (`DbSeeder.SeedAsync`), e o workflow só reporta sucesso quando o endpoint `/health` responde.

O deploy de `develop` continua construindo a imagem na própria máquina e não gera versão. Versões
numeradas são publicadas pelo `.github/workflows/release.yml`, a partir de tags SemVer em `main`, em
runners hospedados, sem os runners self-hosted de deploy (ver
[Releases e versionamento](#releases-e-versionamento)).

### Proteção de branches

`main` e `develop` têm Repository Rulesets ativos (`Protect main` e `Protect develop`, um por
branch, `enforcement: active`). As demais branches (`feature/*`, `fix/*`, `chore/*`...) não têm regras.

- Push direto não é aceito: toda alteração entra por Pull Request (merge, squash ou rebase).
- Force push e exclusão das duas branches estão bloqueados.
- Todas as conversations do PR precisam estar resolvidas antes do merge.
- Não há aprovação humana obrigatória (0 aprovações) enquanto o projeto é mantido
  individualmente; o quality gate é o CI.
- Checks obrigatórios, todos verdes, publicados pelo GitHub Actions: `build-and-test`,
  `sqlserver-integration` e `deploy-scripts`. Os jobs de `backend-deploy.yml` não são checks de PR.
- A branch do PR precisa estar atualizada com a branch de destino (`strict`).
- Sem bypass: nenhum usuário, admin, app ou deploy key ignora as regras. Mudanças de política
  são feitas conscientemente nas configurações do ruleset.

Como o merge de `develop` em `main` cria um merge commit que só existe em `main`, o PR
seguinte `develop → main` aparece desatualizado. O botão "Update branch" desse PR tentaria
um push direto em `develop` e é bloqueado. Nesse caso, abra antes um PR `main → develop`
(back-merge) e faça o merge dele.

## Releases e versionamento

O projeto usa [Semantic Versioning](https://semver.org/lang/pt-BR/). Uma release oficial é criada por
uma tag anotada `vMAJOR.MINOR.PATCH` (ex.: `v0.1.0`) num commit de `main`. Merge em `main`, sozinho,
não gera versão. O push da tag dispara `.github/workflows/release.yml`, que:

1. valida o formato da tag e confirma que o commit pertence a `main`;
2. constrói a imagem da API uma única vez e aplica o gate do Trivy;
3. publica essa mesma imagem no GHCR como `ghcr.io/lucasdevolps/almirante-api:vX.Y.Z` e
   `:sha-<commit>`, e confere o digest;
4. cria a GitHub Release com commit, imagens, digest, SBOM e as notas geradas pelo GitHub.

Não existe tag `latest`. Deploy e rollback de uma release com Docker Compose usam o overlay
`compose.release.yaml`, de preferência com a imagem por digest. Voltar para uma imagem anterior
**não** reverte migrations. SemVer, criação de tags, rastreabilidade, rollback, migrations e
configuração manual no GitHub estão em [docs/release-process.md](docs/release-process.md).

## Contribuição e governança

Antes de contribuir, consulte:

- [Guia de contribuição](CONTRIBUTING.md) — branches, commits, Pull Requests, testes e regras para banco e segurança;
- [Política de segurança](SECURITY.md) — como reportar vulnerabilidades de forma privada.

Pull Requests e issues têm templates próprios em `.github/`.

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

O Swagger recebe uma Content-Security-Policy própria (scripts só da própria origem); as demais rotas usam a CSP restritiva da API JSON. Como a documentação expõe o contrato da API, essa decisão é adequada ao MVP interno atual. Antes de uma exposição pública, recomenda-se restringir o acesso por autenticação, rede ou configuração de ambiente.

## Segurança (issues #29, #31, #33, #34, #35 e #36)

Resumo: RBAC por policies com `401`/`403` distintos; rate limiting e lockout do login na própria API; segredos fora do Git com validação no startup e política de senha; `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, CSP e HSTS (fora de Development, em HTTPS) em todas as respostas. **A autenticação exige TLS** (cookies `Secure`): em HTTP puro, `csrf`/`login` respondem `500`. A API também recusa iniciar em `Production` se a connection string do SQL Server tiver `TrustServerCertificate=True` ou `Encrypt=False` (#36). Detalhes, decisões, rotação de segredos expostos e pendências em [`docs/authentication-security.md`](docs/authentication-security.md).

O login agora retorna somente o envelope `token`; o perfil atual vem de `GET /api/Auth/Me`. Sessões e hashes de refresh tokens são persistidos no SQL Server, refresh é rotativo por cookie seguro e logout revoga a sessão apresentada. O fluxo de frontend, configuração Base64/kid, rotação, migração, TLS e riscos residuais estão em [`docs/authentication-security.md`](docs/authentication-security.md). Tokens emitidos antes desta mudança não têm `sid` e exigem novo login.
