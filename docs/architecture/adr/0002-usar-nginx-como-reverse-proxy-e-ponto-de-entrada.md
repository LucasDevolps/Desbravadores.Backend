# ADR-0002: Usar Nginx como reverse proxy e único ponto de entrada HTTP dos deploys

## Status

Accepted (registro retroativo em 2026-09-23). Introduzido no PR #30; TLS de ambiente publicado
adicionado na issue #50.

## Contexto

O PR #30 descreve o problema: `POST /api/Auth/login` não tinha proteção contra tentativas repetidas,
e o Docker Compose publicava a API diretamente no host (`ports: 8090:8080`). Com a API exposta,
qualquer limite aplicado só na borda poderia ser contornado acessando a API diretamente.

A API também precisa conhecer o IP real do cliente e o esquema original (HTTP ou HTTPS). Esses dados
alimentam o rate limit por IP, o IP gravado na auditoria, os cookies `Secure` e o HSTS. Atrás de um
proxy, esses dados chegam em headers que um cliente também pode forjar.

## Decisão

Nos dois caminhos de deploy versionados, o Nginx fica na frente da API e é o único processo que
recebe tráfego HTTP de fora.

**Docker Compose** ([`compose.yaml`](../../../compose.yaml))

- O serviço `nginx` (`nginx:1.27-alpine`) é o único que publica porta HTTP no host
  (`API_HOST_PORT`, padrão 8090, para a porta 80 do container).
- O serviço `api` usa apenas `expose: "8080"` e só é alcançável pela rede interna `almirante-net`
  (`172.30.0.0/24`). O nginx repassa para `api:8080` em HTTP.
- O nginx termina o TLS. Existem dois overlays opt-in, que não devem ser combinados:
  - `compose.https.yaml`: desenvolvimento local, com certificado autoassinado e porta 443 ligada a
    `127.0.0.1` (padrão 8443), no mesmo `server` de `nginx/nginx.conf`.
  - `compose.tls.yaml` com `nginx/nginx.tls.conf`: ambiente publicado. A porta 80 só redireciona
    (`308`) para 443, com TLS 1.2 e 1.3. Somente o host de `TLS_PUBLIC_HOST` é aceito; os demais
    recebem `421`.
- O nginx aplica rate limit por IP da conexão TCP (`$binary_remote_addr`), independente de headers:
  - `/api/auth/login`: 3 requisições por minuto com `burst=2`;
  - `/api/auth/refresh`: 30 por minuto com `burst=9`.
  Ambos os `location` são case-insensitive e respondem `429` em `application/problem+json` com
  `Retry-After: 60`.
- O nginx envia `Host`, `X-Real-IP`, `X-Forwarded-For` (`$proxy_add_x_forwarded_for`) e
  `X-Forwarded-Proto`.

**API**

- `UseForwardedHeaders` é o primeiro middleware registrado em `Program.cs`. Ele só processa
  `X-Forwarded-For`/`X-Forwarded-Proto` quando `ReverseProxy:TrustedNetworkCidr` está configurado e
  a conexão imediata vem dessa rede, com `ForwardLimit = 1` e `KnownProxies` limpo.
- A startup recusa uma rede confiável mais larga que `/16` (IPv4) ou `/64` (IPv6).
- Sem essa configuração, os headers são ignorados.
- Os cabeçalhos de segurança (CSP, HSTS, `nosniff`, `X-Frame-Options`, `Referrer-Policy`) são
  emitidos pela API. O nginx só os adiciona às respostas `429` que ele mesmo gera.

**Windows/IIS** ([`backend-deploy.yml`](../../../.github/workflows/backend-deploy.yml), job
`deploy-windows`)

- O nginx roda nativo (`nginx/nginx.windows.conf`) em `127.0.0.1:8090`, na frente do site do IIS em
  loopback, com os mesmos limites de login e refresh.
- O workflow grava `ReverseProxy:TrustedNetworkCidr = 127.0.0.1/32` no `appsettings.Production.json`
  do servidor.

**Fora do escopo desta decisão**

- A execução pelo .NET Aspire não usa nginx. A API é acessada diretamente
  ([ADR-0008](0008-usar-aspire-no-desenvolvimento-e-opentelemetry-na-api.md)).
- O rate limit e o lockout feitos pela própria API existem de forma independente do nginx
  ([`docs/authentication-security.md`](../../authentication-security.md#proteção-contra-força-bruta-33)).

## Consequências

### Positivas

- No Compose, o limite de tentativas de login não pode ser contornado acessando a API por outra porta,
  porque a API não publica porta no host.
- Certificados e política TLS ficam concentrados no proxy. A API não gerencia certificados.
- `HttpContext.Connection.RemoteIpAddress` reflete o IP real do cliente sem aceitar
  `X-Forwarded-For` forjado. Esse IP alimenta o rate limit da API e o IP gravado pela auditoria
  ([ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md)).
- O proxy rejeita requisições em excesso antes de chegarem à API, sem consulta ao banco.

### Negativas / trade-offs

- O tráfego entre nginx e API é HTTP em texto claro dentro da rede Docker. A proteção TLS cobre a
  borda externa, não a rede interna do Compose.
- Existem três configurações de nginx mantidas em paralelo (`nginx.conf`, `nginx.tls.conf` e
  `nginx.windows.conf`), com os mesmos limites repetidos. `scripts/tests/nginx.test.sh` valida as três
  no CI.
- O login é limitado em duas camadas com números diferentes: 3 por minuto no nginx e 5 por minuto na
  API (padrão). Os contadores das duas camadas ficam em memória e valem por processo.
- A confiança nos headers depende de `API_TRUSTED_PROXY_CIDR` coincidir com a subnet da rede do
  Compose. Se a subnet mudar, as duas configurações precisam mudar juntas.
- Inferido de `location /`: `/health`, `/alive` e, quando habilitado, `/swagger` são repassados pelo
  nginx sem restrição adicional.
- No Windows, a tarefa agendada executa o nginx como `SYSTEM`. O próprio workflow registra isso como
  risco residual conhecido.
- O desenvolvimento pelo Aspire não exercita o proxy nem os limites do nginx.

## Alternativas consideradas

- **Publicar a API diretamente no host** (estado anterior ao PR #30). O PR o substituiu porque um
  limite aplicado só no proxy seria contornável pela porta da API.

Outras alternativas avaliadas, como outro reverse proxy, não foram encontradas no repositório.

## Evidências no repositório

- [`compose.yaml`](../../../compose.yaml): serviços `nginx` (`ports`) e `api` (`expose`), rede
  `almirante-net`, `ReverseProxy__TrustedNetworkCidr`.
- [`compose.https.yaml`](../../../compose.https.yaml) e [`compose.tls.yaml`](../../../compose.tls.yaml).
- [`nginx/nginx.conf`](../../../nginx/nginx.conf), [`nginx/https.conf`](../../../nginx/https.conf),
  [`nginx/nginx.tls.conf`](../../../nginx/nginx.tls.conf) e
  [`nginx/nginx.windows.conf`](../../../nginx/nginx.windows.conf).
- [`Program.cs`](../../../backend/Almirante.Api/Program.cs): `ForwardedHeadersOptions`,
  `app.UseForwardedHeaders()` e a ordem do pipeline.
- [`ReverseProxyOptions.cs`](../../../backend/Almirante.Api/Options/ReverseProxyOptions.cs) e
  [`SecurityHeadersMiddleware.cs`](../../../backend/Almirante.Api/Security/SecurityHeadersMiddleware.cs).
- [`backend-deploy.yml`](../../../.github/workflows/backend-deploy.yml): jobs `deploy-linux` e
  `deploy-windows`. Os modos `http` e `tls` são escolhidos por
  [`scripts/deploy-config.sh`](../../../scripts/deploy-config.sh).
- Testes: [`ForwardedHeadersTests.cs`](../../../backend/Almirante.Api.Tests/ForwardedHeadersTests.cs),
  [`LoginProtectionTests.cs`](../../../backend/Almirante.Api.Tests/LoginProtectionTests.cs)
  (`RateLimit_IgnoraXForwardedForDeOrigemNaoConfiavel`),
  [`SecurityRegressionTests.cs`](../../../backend/Almirante.Api.Tests/SecurityRegressionTests.cs)
  (`TrustedNetworkCidr_AmploDemais_RecusaIniciar`) e
  [`scripts/tests/nginx.test.sh`](../../../scripts/tests/nginx.test.sh).
- Documentação: [`README.md`](../../../README.md#arquitetura-com-nginx-docker-compose) e
  [`docs/authentication-security.md`](../../authentication-security.md#tls-no-ponto-de-entrada-do-deploy-50).
- Histórico: PR #30 (commit `32c9057`).

## Decisões relacionadas

- [ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md): os cookies `Secure` da
  autenticação dependem do HTTPS na borda.
- [ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md): a auditoria usa o IP
  resolvido pelo middleware de forwarded headers.
