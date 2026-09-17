# Autenticação, sessões e implantação segura

Esta entrega **amplia expressamente a issue #29**. Além de separar login e perfil, adiciona
armazenamento seguro, renovação, sessões persistidas e revogação efetiva; a issue deixava sessões e
revogação fora do MVP. O fluxo é próprio da aplicação e **não** implementa OAuth 2.0/OIDC. A RFC 9700
é usada só como referência para proteger e rotacionar refresh tokens; migrar para um provedor OIDC é
outro trabalho, com impacto nos consumidores.

## Requisitos, decisões e valores configuráveis

| Tema | Requisito de norma | Decisão deste projeto | Configurável |
| --- | --- | --- | --- |
| Integridade do JWT | Assinatura e validação de `alg` antes de confiar no conteúdo (RFC 7515, RFC 8725 §3.1) | HS256 com chave simétrica; somente `alg=HS256` e `typ=at+jwt` aceitos | chaves e `kid` ativo |
| Conteúdo do JWT | JWS não é confidencial; minimizar dados pessoais (RFC 7519 §12) | só `sub`, `role`, `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`, `sid`; nenhum nome, e-mail ou IP | emissor e audiência |
| Datas | `exp`/`nbf`/`iat` como NumericDate (RFC 7519 §2, §4.1) | datas em segundos inteiros; `expiresAtUtc` = `exp`; tolerância de relógio de 30 s | não |
| Validade | — | access token 10 min; sessão absoluta 7 dias; inatividade 24 h entre login/refresh | `AccessTokenMinutes` (1–30), `AbsoluteSessionDays` (1–30), `RefreshInactivityHours` (1–168) |
| Tamanho da chave HMAC | ≥ 256 bits para HS256 (RFC 7518 §3.2) | 32 bytes de CSPRNG em Base64; recusa texto legível e bytes repetidos | `Jwt:Keys:<kid>` |
| Refresh token | proteção e detecção de reuso na rotação (RFC 9700 §4.14.2) | opaco com 32 bytes, SHA-256 `binary(32)` no SQL Server, rotação a cada uso; reuso revoga a família | prazos acima |
| Cookie | — (OWASP Session Management) | `__Host-`, `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, sem `Domain` | não |
| CSRF | — (OWASP CSRF Prevention) | antiforgery do ASP.NET Core em login, refresh e logout; token sem vínculo com o bearer | não |

HS256 é adequado aqui porque emissão e validação ficam no mesmo serviço. Não é vulnerável por ser
simétrico: depende de chave forte, protegida e distribuída só às réplicas da API. Se validadores
separados passarem a existir, prefira RS256/ES256 e distribua apenas a chave pública.

## Perfil e validação do access token

Cada requisição protegida passa, nesta ordem:

1. **Validação criptográfica e de perfil do framework** (`JsonWebTokenHandler`): assinatura HS256 com
   a chave do `kid` (kid desconhecido ou ausente falha sem testar outras chaves; `jku`/`jwk` do
   header são ignorados), `alg` e `typ` permitidos, emissor, audiência, `exp` obrigatório, tempo de
   vida com tolerância de 30 s e tamanho máximo de 8 KB.
2. **Verificação estrutural limitada** (`JwtProfile`): claim crítica duplicada no JSON, `exp`/`iat`/
   `nbf` que não sejam NumericDate inteiros, claim obrigatória ausente ou repetida, `sub`/`sid` que não
   sejam Guid, `iat` mais de 30 s no futuro ou `nbf` posterior a `exp` invalidam o token.
3. **Estado da sessão** (uma consulta indexada ao SQL Server): sessão existente, não revogada e dentro
   do prazo absoluto, do mesmo `sub`, usuário existente com a mesma `SecurityVersion` e cargo ativo com
   a mesma `role` do token.

`sub` é mapeado explicitamente para `ClaimTypes.NameIdentifier` (usado por `/Me`, pela auditoria dos
lançamentos e pelas policies). `role` é o `RoleClaimType`. `SaveToken=false`. O token é reutilizável
durante a validade enquanto a sessão estiver ativa: `jti` não impede replay e não há invalidação
após o primeiro uso.

## Chaves: formato, provisionamento, rotação e comprometimento

Formato: `Jwt:ActiveKeyId` (kid usado para assinar) e `Jwt:Keys:<kid>` com Base64 de **32 bytes
aleatórios** (`openssl rand -base64 32`). A API não inicia se a chave ativa não existir ou se qualquer
chave for Base64 inválido, tiver menos de 32 bytes, for texto legível ou tiver bytes repetidos.
Nenhuma chave é gerada automaticamente na inicialização.

| Ambiente | Onde fica a chave (nunca no Git, na imagem, no banco ou em logs) |
| --- | --- |
| Desenvolvimento com Aspire | User Secrets do AppHost: `dotnet user-secrets set "Parameters:jwt-key-v1" "$(openssl rand -base64 32)" --project backend/Almirante.AppHost` |
| Docker Compose (Linux) | `.env` da máquina (`JWT_KEY_V1`, `JWT_ACTIVE_KEY_ID`), fora da pasta clonada, com permissão restrita ao usuário do runner |
| IIS (Windows) | `appsettings.Production.json` do site (não sobrescrito pelo deploy), com ACL restrita à conta do pool; o workflow confere a presença antes de publicar |

GitHub Actions Secrets só entregam o valor ao processo do workflow que o injeta: não são um cofre
acessível pela aplicação em execução.

**Rotação planejada** (exemplo v1 → v2):

1. Gere a v2 e adicione `Jwt:Keys:v2` em **todas** as réplicas, mantendo `ActiveKeyId=v1`. Reinicie.
2. Troque `ActiveKeyId` para `v2` em todas as réplicas. Novos tokens saem com `kid=v2`; tokens v1
   continuam válidos.
3. Depois da validade máxima de um access token mais a tolerância (10 min + 30 s com os padrões),
   remova `Jwt:Keys:v1`.

Uma réplica que ainda não tem a v2 recusa tokens v2 (401): por isso distribuir antes de ativar.

**Comprometimento**: remova imediatamente a chave comprometida de todas as réplicas e ative uma nova.
Tokens assinados com ela passam a ser recusados. Se houver suspeita de sessões forjadas, incremente
`Usuarios.SecurityVersion` dos usuários afetados (ou de todos) para revogar as sessões existentes e
exigir novo login.

As chaves do **Data Protection** (usadas pelo antiforgery) são outra coisa: ficam no volume
`almirante-dataprotection` (diretório 700 do usuário `app`) no Compose e no perfil do pool no IIS. São
persistidas para que reinícios não invalidem o CSRF. Com várias réplicas, todas precisam do mesmo
diretório/volume e do mesmo nome de aplicação (`Almirante:<ambiente>`). No Linux, as chaves ficam sem
criptografia em repouso dentro desse volume privado (ver riscos residuais).

## Onde cada credencial fica

| Informação | Local |
| --- | --- |
| Access token no navegador | memória da SPA; enviado em `Authorization: Bearer` |
| Refresh token no navegador | cookie `__Host-almirante-refresh` definido pelo servidor |
| Token CSRF no navegador | memória da SPA (o cookie antiforgery par é HttpOnly) |
| Access token no servidor | não é persistido; só a sessão (`AuthSessions`) |
| Refresh token no servidor | apenas SHA-256 (`RefreshTokens.TokenHash`, índice único) e metadados |
| Perfil | `GET /api/Auth/Me`, sempre do banco |

Nada de `localStorage`, `sessionStorage`, IndexedDB, URL, query string ou analytics para credenciais.
`HttpOnly` dificulta a extração do refresh por JavaScript, mas não impede ações de um XSS dentro da
página.

## Cookies, CSRF, HTTPS e CORS

- **HTTPS obrigatório**: `GET /csrf`, `POST /login`, `POST /refresh` e `POST /logout` respondem
  `400 HTTPS obrigatório.` fora de HTTPS, sem definir cookies. Não existe exceção para HTTP, nem em
  desenvolvimento: use o perfil `https`, o nginx com TLS ou um terminador TLS que envie
  `X-Forwarded-Proto: https` a partir de um IP confiável (`ReverseProxy:TrustedNetworkCidr`).
- **Cookie de refresh**: expira no prazo real da credencial, `min(última renovação + 24 h, prazo
  absoluto)`, em UTC. É apagado com o mesmo nome, caminho e atributos no logout e em refresh recusado.
- **CSRF**: `GET /api/Auth/csrf` devolve `{ "csrfToken": "..." }` e define o cookie antiforgery.
  Login, refresh e logout exigem o par cookie + header `X-CSRF-TOKEN`. O token é emitido e validado sem
  vínculo com o bearer, porque a credencial protegida é o cookie: o mesmo token vale com bearer
  válido, expirado ou ausente, e depois de um logout. Isso também vale para páginas do mesmo site em
  outra origem, que recebem os cookies `SameSite=Strict` mas não conseguem ler o token.
- **SameSite=Strict** exige que a SPA e a API sejam o mesmo *site* com o mesmo esquema: por exemplo
  `https://app.exemplo` e `https://api.exemplo`, ou `https://localhost:4200` e `https://localhost:8443`.
  Uma SPA em `http://localhost:4200` não consegue logar numa API HTTPS: o navegador não grava nem
  envia o cookie. Em desenvolvimento, sirva o front em HTTPS ou use um proxy de desenvolvimento na
  mesma origem.
- **CORS**: lista exata em `Cors:AllowedOrigins` (padrão `https://localhost:4200` e `:4201`), com
  credenciais. Nunca use `*` (o ASP.NET Core recusa curinga com credenciais). CORS e SameSite
  complementam o antiforgery e não o substituem.
- **Cache**: todas as respostas de `/api/Auth/*` têm `Cache-Control: no-store`, inclusive erros.

## Sessões, renovação e revogação

- **Login** cria uma sessão nova (id gerado pelo servidor), grava o refresh e só então entrega as
  credenciais.
- **Refresh** (`POST /api/Auth/refresh`, sem bearer) localiza o hash e valida sessão, prazos, cargo
  ativo e `SecurityVersion`. Em seguida, na mesma transação, marca o token atual como consumido,
  aponta o sucessor e cria um novo. `rowversion` impede dois sucessores válidos: a renovação que
  perde a corrida não recebe credencial.
- **Política estrita de reuso**: apresentar um refresh já consumido revoga a família inteira. Isso
  também acontece com duas renovações legítimas simultâneas e quando a resposta de uma renovação se
  perde e o cliente repete o cookie anterior. Nos três casos o usuário precisa logar de novo. Não há
  tolerância para repetição.
- **Revogação** (logout, reuso, renovação concorrente) é idempotente: relê a sessão e reaplica sobre
  o estado atual em caso de conflito. É gravada com `CancellationToken.None`, então abortar a
  requisição não a desfaz. Logout aceita o cookie de refresh e/ou o bearer da própria sessão; nunca
  aceita `sid`/usuário vindos do corpo. Logout sem credencial ou repetido responde `204`.
- **Efeito imediato**: depois do commit da revogação, novas requisições com o access token dessa
  sessão recebem 401 em qualquer réplica, porque não há cache de sessões. Operações já em execução
  não são canceladas.
- **Mudanças de segurança**: incrementar `Usuarios.SecurityVersion` invalida todas as sessões do
  usuário. `Cargo.Ativo=false` bloqueia login, refresh e tokens existentes. Mudar a role invalida o
  access token antigo (a role do token precisa bater com a atual); a sessão continua e o próximo
  refresh emite a role nova. Hoje não existem endpoints de troca de senha, de cargo ou de bloqueio;
  quando existirem, devem incrementar `SecurityVersion` na mesma transação da alteração.
- **Retenção**: sessões e hashes consumidos ficam até o prazo absoluto + 1 h, necessários para
  detectar reuso. `AuthSessionCleanup` remove em lotes a cada 6 h; é seguro rodar em várias réplicas.

## Integração do frontend

Exemplo em TypeScript com `fetch`. Em Angular, a mesma lógica vai num `HttpInterceptor`, com
`withCredentials: true` nas chamadas a `/api/Auth/*`.

```ts
const API = 'https://localhost:8443';
let accessToken: string | null = null;
let expiresAt = 0;
let csrfToken: string | null = null;
let renovacao: Promise<boolean> | null = null;
const canal = new BroadcastChannel('almirante-auth'); // avisa outras abas, sem Web Storage

async function obterCsrf(forcar = false): Promise<string> {
  if (!csrfToken || forcar) {
    const r = await fetch(`${API}/api/Auth/csrf`, { credentials: 'include' });
    if (!r.ok) throw new Error('csrf-indisponivel');
    csrfToken = (await r.json()).csrfToken;
  }
  return csrfToken!;
}

// POST com cookie + CSRF; se o CSRF foi recusado (400, ex.: chaves rotacionadas), busca outro e tenta uma vez.
async function postComCookie(path: string, body?: unknown): Promise<Response> {
  for (let tentativa = 0; tentativa < 2; tentativa++) {
    const r = await fetch(`${API}${path}`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'X-CSRF-TOKEN': await obterCsrf(tentativa > 0), ...(body ? { 'Content-Type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (r.status !== 400) return r;
    const problema = await r.clone().json().catch(() => null);
    if (problema?.title !== 'Proteção CSRF inválida.') return r;
  }
  throw new Error('csrf-recusado');
}

function guardarToken(json: { token: { accessToken: string; expiresAtUtc: string } }) {
  accessToken = json.token.accessToken;
  expiresAt = Date.parse(json.token.expiresAtUtc);
}

function limparEstado() {
  accessToken = null;
  expiresAt = 0;
}

export async function login(email: string, senha: string) {
  const r = await postComCookie('/api/Auth/login', { email, senha });
  if (r.status === 429) throw new Error('muitas-tentativas'); // respeite Retry-After
  if (!r.ok) throw new Error('credenciais-invalidas');
  guardarToken(await r.json());
  canal.postMessage('sessao-alterada');
  return carregarPerfil();
}

// Uma renovação por vez nesta aba e, com Web Locks, entre abas: a segunda aba espera e usa o cookie já
// rotacionado. Evita a revogação por renovação concorrente.
function renovar(): Promise<boolean> {
  renovacao ??= navigator.locks
    .request('almirante-refresh', async () => {
      const r = await postComCookie('/api/Auth/refresh');
      if (!r.ok) {
        limparEstado();
        return false;
      }
      guardarToken(await r.json());
      return true;
    })
    .finally(() => (renovacao = null));
  return renovacao;
}

export async function carregarPerfil() {
  const r = await apiFetch('/api/Auth/Me');
  return r.ok ? r.json() : null; // perfil sempre de /Me, nunca decodificando o JWT
}

// Renova perto do vencimento e, no máximo uma vez, após 401 de token inválido. Escritas não são
// repetidas automaticamente: podem já ter sido executadas.
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  if (!accessToken || Date.now() > expiresAt - 60_000) {
    if (!(await renovar())) throw new Error('nao-autenticado');
  }
  const enviar = () => fetch(`${API}${path}`, { ...init, headers: { ...init.headers, Authorization: `Bearer ${accessToken}` } });
  const r = await enviar();
  const tokenInvalido = r.status === 401 && (r.headers.get('WWW-Authenticate') ?? '').includes('invalid_token');
  const leitura = !init.method || init.method === 'GET' || init.method === 'HEAD';
  if (tokenInvalido && leitura && (await renovar())) return enviar();
  if (tokenInvalido) limparEstado();
  return r; // 401 com title "ACESSO NEGADO!" = sem permissão para a role; não renove
}

export async function restaurarSessao() {
  return (await renovar()) ? carregarPerfil() : null; // após reload: CSRF novo + refresh + /Me
}

export async function logout() {
  await postComCookie('/api/Auth/logout').catch(() => undefined);
  limparEstado();
  canal.postMessage('sessao-alterada');
}

canal.onmessage = () => {
  limparEstado(); // outra aba logou ou saiu: a próxima chamada renova (ou pede login)
};
```

Regras: não repetir refresh em laço (um 401 do refresh encerra a sessão local), não repetir
automaticamente `POST`/`PUT`/`DELETE`, tratar `429` respeitando `Retry-After` e nunca usar o perfil
em memória para decidir permissões: quem autoriza é o backend.

## Implantação, migração e rollback

Ordem recomendada:

1. **Backup** do banco.
2. **Segredos e certificados** em cada ambiente:
   - **Compose (Linux)**: no `.env` da máquina, `JWT_KEY_V1`, `JWT_ACTIVE_KEY_ID=v1`,
     `API_HTTPS_HOST_PORT`, `NGINX_CERTS_DIR` (caminho absoluto com `tls.crt`/`tls.key`, fora do
     checkout), `API_TRUSTED_PROXY_CIDR=172.30.0.10/32` e origens CORS HTTPS. As variáveis antigas
     `JWT_KEY` e `JWT_EXPIRATION_MINUTES` deixam de ser lidas; `docker compose` falha sem `JWT_KEY_V1`.
     Se já existir um volume `almirante-dataprotection` criado como root, corrija uma vez:
     `docker run --rm -u 0 -v almirante-dataprotection:/dp alpine chown -R 1654:1654 /dp`.
   - **IIS (Windows)**: `Jwt:ActiveKeyId` e `Jwt:Keys:v1` em `appsettings.Production.json` do site.
     O certificado do nginx é gerado pelo workflow; a entrada passa a ser `https://127.0.0.1:8443`.
   - **Aspire**: parâmetro `jwt-key-v1` nos User Secrets do AppHost.
3. **Migrations**: `20260917000000_AddAuthenticationSessions` e `20260917120000_UnifyLancamentosFlow`
   são aplicadas na inicialização (`DbSeeder`). Num banco que já tem a `UnifyLancamentosFlow`, o EF
   aplica a `AddAuthenticationSessions` mesmo com timestamp anterior (validado a partir de um banco
   criado pela `main`, com dados preservados). Para aplicar antes do deploy, gere um script idempotente
   com `dotnet ef migrations script --idempotent`.
4. **Backend e frontend juntos**: o login deixa de devolver `usuario`, passa a exigir CSRF e HTTPS, e
   tokens emitidos antes da mudança (sem `sid`, `kid` e `typ`) são recusados; todos precisam logar de
   novo.
5. **Validação**: `scripts/e2e/compose-smoke.sh` na stack Compose e `GET /health`.

**Rollback**: voltar o binário da API (e o frontend) para a versão anterior funciona sobre o banco já
migrado: as tabelas `AuthSessions`/`RefreshTokens` e a coluna `SecurityVersion` são ignoradas pela
versão antiga (validado). Não é preciso reverter as migrations. Voltar o nginx exige restaurar a
configuração anterior (HTTP), o que desliga o novo fluxo de login. Um roll-forward depois do rollback
exige novo login, porque os tokens da versão antiga são recusados.

## Observabilidade

Eventos de segurança (`Almirante.Api.Services.AuthService`), sem senha, tokens, cookies ou e-mail
informado:

| EventId | Nome | Nível |
| --- | --- | --- |
| 1001 | `LoginFalhou` | Information |
| 1002 | `LoginRealizado` (usuário, sessão) | Information |
| 1003 | `RefreshRecusado` (sessão) | Information |
| 1004 | `ReusoDeRefreshDetectado` (sessão, usuário) | Warning |
| 1005 | `RenovacaoConcorrente` (sessão) | Warning |
| 1006 | `SessaoRevogada` (sessão, motivo) | Information |

A correlação vem do TraceId do OpenTelemetry. Os comandos SQL do EF ficam em `Warning` por padrão
(`Microsoft.EntityFrameworkCore.Database.Command`); em nível Information cada requisição autenticada
registraria a consulta de sessão. Parâmetros SQL (como o hash do refresh) não são registrados porque
`EnableSensitiveDataLogging` não é usado. O nginx não registra headers `Authorization`/`Cookie`.

## Desempenho medido

A validação de sessão é uma consulta por requisição autenticada, com Index Seek nas chaves primárias
de `AuthSessions`, `Usuarios` e `Cargos`, e sem escrita de "último acesso". Medições locais (uma
máquina rodando gerador de carga, Docker e SQL Server, então valem como ordem de grandeza e não como
capacidade):

- SQL Server: média de **0,055 ms** e **6 leituras lógicas** por execução (68 mil execuções sob carga);
- Kestrel sequencial: **+1,7 ms** no p50 de um endpoint com bearer em relação ao mesmo endpoint anônimo;
- 40 usuários concorrentes via nginx (TLS): 4.039 req/s anônimo contra 2.202 req/s com bearer (p50
  9,0 → 16,8 ms), sem erros. Sem `keepalive` no upstream do nginx, a mesma carga esgotava portas e
  gerava 502.

Não há cache de sessões. Se o volume justificar um cache, ele precisa de invalidação entre instâncias,
e a janela de atraso da revogação deve ser documentada.

## Testes

- `dotnet test backend/Almirante.Api.Tests --filter "Category!=RequiresDocker"`: contrato, matriz de
  validação do JWT, CSRF, HTTPS, prazos com relógio controlável, enumeração, chaves, logs e OpenAPI.
- `--filter "Category=RequiresDocker"` (Testcontainers ou `ALMIRANTE_TESTS_SQLSERVER`): duas instâncias
  sobre o mesmo SQL Server, com refresh e logout concorrentes, reuso, revogação vista pela outra
  instância, limpeza em lotes e migrations em banco novo.
- `scripts/e2e/compose-smoke.sh`: topologia real (TLS, Data Protection no volume, confiança no proxy,
  rate limit, carga).

## Riscos residuais e pendências externas

- **Certificados**: em produção, o certificado do nginx precisa ser confiável e renovado fora da
  aplicação (ACME ou terminador gerenciado). O autoassinado do Windows serve só para loopback.
- **Segredos em variáveis de ambiente** (chave JWT, senha do SQL, senha do seed) ficam visíveis para
  quem tem acesso ao Docker (`docker inspect`) ou ao processo. Arquivos montados (`/run/secrets`) ou
  um cofre reduziriam essa exposição.
- **Chaves do Data Protection** sem criptografia em repouso no volume do Compose. Com várias máquinas,
  use um repositório compartilhado protegido (por exemplo `ProtectKeysWithCertificate`).
- **Acesso direto à API**: processos no host Docker ou containers na mesma rede alcançam a API sem o
  nginx. As rotas de cookie recusam essas chamadas e o IP forjado não é aceito, mas endpoints com
  bearer continuam acessíveis por esse caminho, fora do rate limit.
- **Rate limit por instância de proxy** e compartilhado por clientes atrás de um mesmo proxy.
- **Política estrita** pode exigir novo login em renovações simultâneas legítimas ou respostas
  perdidas; a coordenação no frontend reduz, mas não elimina, esses casos.
- **401 "ACESSO NEGADO!"** para usuário autenticado sem permissão foi preservado por contrato. O usual
  seria 403; mudar exige coordenar com os consumidores.
- **Swagger público** em todos os ambientes (decisão anterior do projeto).
- **Volume `almirante-sqlserver-data`** tem o mesmo nome no AppHost e no Compose; numa máquina que use
  os dois, defina `SQL_DATA_VOLUME`.
