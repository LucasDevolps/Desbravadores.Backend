# Autenticação, autorização e implantação segura

Este documento cobre as issues #29 (login/perfil/JWT), #31 (RBAC), #33 (força bruta), #34 (segredos e política de senha), #35 (cabeçalhos HTTP), #36 (TLS do SQL Server em produção) e #50 (TLS no ponto de entrada do deploy, rotação de segredos expostos, Data Protection em Linux e auditoria do `PUT` de lançamentos). Sessões persistidas, refresh token rotativo e revogação foram adicionados antes (PR #46) e são preservados. O fluxo é próprio da aplicação e **não** é apresentado como OAuth 2.0/OIDC.

## Login, perfil e JWT (#29)

- `POST /api/Auth/login` responde `200` somente com `{"token":{"accessToken":"…","expiresAtUtc":"…Z"}}`. Credenciais inválidas, e-mail inexistente, conta bloqueada e cargo inativo recebem o **mesmo** `401` (`{"title":"Credenciais inválidas.","status":401}`). Para e-mail inexistente a API verifica um hash fictício, para que o custo do PBKDF2 não denuncie contas existentes.
- `GET /api/Auth/Me` devolve o perfil atual do banco (`id`, `nome`, `email`, `cargo{id,nome,role}`), identificado **apenas** pela identidade autenticada; parâmetros enviados pelo cliente são ignorados. Senha, hash e metadados de auditoria não fazem parte do DTO. Retorna `401` sem autenticação válida, com identificador inválido ou usuário inexistente.
- Login, refresh, logout e `/Me` respondem com `Cache-Control: no-store` (inclusive em erros).
- O access token usa HS256 porque emissão e validação ocorrem na mesma API. Payload: `sub`, `role`, `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`, `sid` — sem nome nem e-mail e com um único identificador do usuário (`sub`). `jti`/`iat`/`nbf` são exigidos pelo perfil local e `sid` vincula o token à sessão persistida (revogação). JWS garante integridade, **não** confidencialidade.
- Validação: assinatura (chave resolvida por `kid`), emissor, audiência, expiração com tolerância de 30 s, `typ=at+jwt`, **somente** `HS256` (`ValidAlgorithms`) e tamanho máximo de 8 KB (`JsonWebTokenHandler` em `TokenHandlers`).
- Mapeamento de claims: `MapInboundClaims=false`; `RoleClaimType="role"`; `NameClaimType="sub"`. `OnTokenValidated` confere que `sub` é um GUID único e adiciona explicitamente `ClaimTypes.NameIdentifier = sub`, consumida por `ClaimsPrincipalExtensions.TentarObterUsuarioId` (Me, lançamentos, auditoria). Não há fallback silencioso para `sub`.
- A cada request, `OnTokenValidated` também exige sessão não revogada e que a `role` do token ainda seja a role do cargo ativo do usuário. Consultar `/Me` não altera a role de um token já emitido; a mudança de cargo invalida o token na requisição seguinte (novo login necessário).
- Logs: nenhum código registra senha, token ou `Authorization`; o IdentityModel oculta PII por padrão e a instrumentação OpenTelemetry do ASP.NET Core não captura headers. Um teste captura logs em nível `Trace` durante login/`/Me` e verifica a ausência desses valores.

## Matriz de acesso (#31)

A matriz existente em Lançamentos (roles `ADM`, `DIR`, `DIRA`, `SEC`, `TES`, PR #41) foi preservada e passou a valer também para as listagens administrativas. Fonte única: `Security/Roles.cs` (`Roles.Diretoria`) e policies registradas em `Program.cs`.

| Endpoint | Sem autenticação válida | Autenticado sem permissão | Permitido |
| --- | --- | --- | --- |
| `GET /api/Auth/Me` | 401 | — | qualquer autenticado (só o próprio perfil) |
| `GET /api/Usuarios` | 401 | 403 | policy `GestaoCadastros`: ADM, DIR, DIRA, SEC, TES |
| `GET /api/Cargos` | 401 | 403 | policy `GestaoCadastros`: ADM, DIR, DIRA, SEC, TES |
| `GET /api/Lancamentos` | 401 | 403 | policy `GestaoFinanceira`: ADM, DIR, DIRA, SEC, TES |
| `POST /api/Lancamentos/Registrar` | 401 | 403 | policy `GestaoFinanceira` |
| `PUT /api/Lancamentos/{id}` | 401 | 403 | policy `GestaoFinanceira` |
| `DELETE /api/Lancamentos/{id}` | 401 | 403 | policy `GestaoFinanceira` |

- `401` vem do challenge do JwtBearer (`WWW-Authenticate: Bearer`); `403` é Problem Details `{"title":"ACESSO NEGADO!"}` (antes era 401, o que levava o cliente a descartar uma sessão válida).
- Decisão a confirmar com o negócio: as policies `GestaoCadastros` e `GestaoFinanceira` hoje têm o mesmo conjunto de roles (o registro de lançamentos precisa listar membros). Separá-las exige só alterar `Program.cs`.
- Auditoria: `LancamentoOperacao.CriadoPorUsuarioId`, `lancamentos_deletados.UsuarioResponsavelId`/`IpResponsavel` e, desde a #50, `Lancamento.AtualizadoPorUsuarioId` (migration `AddAtualizadoPorUsuarioIdToLancamentos`) vêm **sempre** da identidade autenticada (`User.TentarObterUsuarioId`, nunca do body) e do IP pós-proxy confiável quando aplicável. `AtualizadoPorUsuarioId` é nullable (`Guid?`, FK `Restrict` para `Usuarios`) para não quebrar lançamentos atualizados antes desta coluna existir; não é exposto em `LancamentoDto` (não é necessário ao frontend). Registra somente o **último** responsável pelo `PUT` — não é um histórico completo de alterações (se isso vier a ser necessário, é escopo de uma issue separada, com uma tabela de auditoria própria).

## Proteção contra força bruta (#33)

Duas camadas na própria API, independentes do nginx:

1. **Rate limiting por IP** (`Microsoft.AspNetCore.RateLimiting`, policy `login` em `POST /api/Auth/login`): janela fixa de `LoginProtection:RateLimit:WindowSeconds` (60 s) com `PermitLimit` (5) tentativas, sem fila. Excedido: `429` Problem Details com `Retry-After` (segundos até a janela reabrir). Conta todas as tentativas, com ou sem sucesso, e roda antes da autenticação e do antiforgery.
   - Partição = `Connection.RemoteIpAddress` **depois** de `UseForwardedHeaders`: `X-Forwarded-For`/`X-Forwarded-Proto` só são aceitos se a conexão vier de `ReverseProxy:TrustedNetworkCidr`, com `ForwardLimit=1` (vale só o valor mais à direita, anexado pelo proxy). Sem proxy configurado, os headers são ignorados.
   - Alcance: contador em memória **por instância**. Com N réplicas o limite efetivo por IP chega a N×. Os deploys atuais têm uma única instância (Compose e IIS); para escalar horizontalmente use armazenamento compartilhado (ex.: Redis) ou limite no balanceador.
2. **Lockout temporário por conta** (colunas `FalhasLoginConsecutivas`, `UltimaFalhaLoginUtc`, `LoginBloqueadoAteUtc` em `Usuarios`, migration `AddLoginLockout`): após `MaxFailedAttempts` (10) falhas de senha dentro de `FailureWindowMinutes` (15), a conta fica bloqueada por `LockoutMinutes` (15), inclusive para a senha correta.
   - Adotado porque o limite por IP não impede tentativas distribuídas entre muitos IPs; como fica no SQL Server, vale para todas as instâncias.
   - Concorrência: o incremento é um único `UPDATE` atômico (`ExecuteUpdate`) que só atua em contas não bloqueadas; testado com 20 tentativas paralelas em SQL Server real (nenhum incremento perdido, contador para exatamente no limite).
   - Recuperação: expira sozinho; tentativas durante o bloqueio não o prolongam; falhas fora da janela reiniciam a contagem; sucesso zera o contador.
   - Enumeração: resposta idêntica ao `401` genérico, sem `Retry-After`; e-mails inexistentes não geram estado.
   - Risco residual aceito: quem conhece um e-mail pode bloqueá-lo por 15 min a cada 10 tentativas (negação de serviço direcionada). Mitigue ajustando os limites ou reforçando o rate limit na borda.
3. **nginx** (Compose e Windows): `limit_req` de 3 tentativas/min por IP da conexão TCP em `location ~* ^/api/auth/login/?$` (cobre variações de caixa), refresh 30/min. Continua sendo a primeira barreira; a da API vale mesmo se o nginx for contornado ou mal configurado.

## Segredos e política de senha (#34)

- Nenhum segredo utilizável é versionado: `appsettings*.json` e `.env.example` só têm placeholders `DEFINA_…`; o AppHost não tem mais senha padrão do SQL Server.
- Startup falha com mensagem clara (sem expor o valor) se `Jwt:Keys` estiver vazio, `Jwt:ActiveKeyId` não existir, a chave for placeholder, Base64 inválido, tiver menos de **32 bytes decodificados** (HS256, RFC 7518 §3.2) ou for trivial (bytes iguais). Comprimento não prova aleatoriedade: gere cada chave com CSPRNG e uma por ambiente. A chave nunca é gerada na inicialização.
- Política de senha (`Security/PasswordPolicy.cs`), aplicada ao **definir** senha — hoje, o bootstrap do admin: 12 a 128 caracteres; ao menos uma letra; não pode ser formada por 1–2 caracteres repetidos, sequência simples, placeholder, raiz trivial (ex.: `senha`, `admin`, `almirante`, mesmo com dígitos/símbolos) ou conter a parte local do e-mail. O hash (PBKDF2-HMAC-SHA512 do `PasswordHasher`) não trunca a senha; o teto de 128 é validado explicitamente.
- O login **não** aplica a política: credenciais existentes continuam válidas pelo hash.
- `SeedAdmin:Senha` só é exigida e validada quando o admin ainda não existe; o seed é idempotente e nunca sobrescreve a senha de um admin existente.

### Configuração local

```bash
openssl rand -base64 32   # gera a chave
dotnet user-secrets set "Jwt:ActiveKeyId" "dev" --project backend/Almirante.Api
dotnet user-secrets set "Jwt:Keys:dev" "<saída do openssl>" --project backend/Almirante.Api
dotnet user-secrets set "SeedAdmin:Senha" "<senha forte>" --project backend/Almirante.Api
dotnet user-secrets set "Parameters:sql-password" "<senha do SQL>" --project backend/Almirante.AppHost
```

No Docker Compose use o `.env` (fora do Git); em publicação, variáveis de ambiente/cofre do ambiente (`Jwt__Keys__v1`, `SeedAdmin__Senha`, …) ou o `appsettings.Production.json` local ao servidor, que o deploy não sobrescreve.

### Rotação de valores expostos (pendência operacional)

Remover um valor do arquivo atual **não** o remove do histórico do Git. Estiveram versionados (`.env.example` e AppHost): uma chave JWT de desenvolvimento, a senha do admin de desenvolvimento e a senha do SA de desenvolvimento — os valores em si não são reproduzidos aqui (nem os antigos, nem os novos gerados numa rotação real). **Qualquer segredo real que já esteve no histórico do Git é tratado como comprometido, sem exceção** — não existe uma categoria intermediária de "só exposto, não comprometido" para decidir depois: se o valor já saiu da máquina de quem o gerou (por ter sido commitado), rotacione-o.

1. **JWT** — `Jwt:Keys` já é um dicionário por `kid` (`JwtOptions.Keys`/`JwtOptionsValidator`) e `compose.yaml` já mapeia `Jwt__Keys__v1`/`Jwt__Keys__v2` como opcionais (issue #50): mudar só o `.env` deste ambiente é suficiente em qualquer uma das situações abaixo, sem editar `compose.yaml`.
   - **Rotação planejada de uma chave que não está comprometida** (ex.: rotina periódica): gere a chave nova com `openssl rand -base64 32` (nunca cole a saída em issue/commit/PR/log ou em qualquer teste compartilhado). Defina `JWT_KEY_V2` no `.env` (fora do Git) mantendo `JWT_KEY_V1`, depois troque `JWT_ACTIVE_KEY_ID=v2`. Tokens de acesso já emitidos com `v1` continuam validando normalmente (o `kid` no token seleciona a chave) até expirarem — espere o **maior valor entre** `Jwt:AccessTokenMinutes`/`JWT_ACCESS_TOKEN_MINUTES` configurado neste ambiente (não assuma 10 min: confira o valor real) **mais** a tolerância de relógio de 30 s (`ClockSkew`, `Program.cs`). Só depois desse prazo, apague `JWT_KEY_V1` do `.env` (deixe vazia ou remova a linha).
   - **Chave comprometida** (exposta no histórico do Git, vazamento, ou qualquer suspeita concreta): não existe janela de transição — troque `JWT_ACTIVE_KEY_ID` para a nova chave e apague a chave comprometida do `.env` imediatamente. Isso por si só **não** desconecta sessões já autenticadas: `AuthService.RefreshAsync` valida o refresh token opaco (hash no banco) e nunca revalida a chave/`kid` do access token antigo, então uma sessão com refresh token válido continua emitindo novos access tokens normalmente mesmo depois da chave de assinatura antiga sumir. Para realmente exigir novo login de todo mundo, revogue as sessões ativas (ver item 2 abaixo, mesmo mecanismo) — a rotação da chave sozinha só bloqueia quem ainda tiver, na mão, um access token específico assinado com a chave removida (janela de no máximo `AccessTokenMinutes` + 30 s).
2. **Senha do admin** — use `dotnet run --project backend/Almirante.Api -- reset-admin-password <email>` (novo nesta entrega, `Cli/AdminPasswordResetCli.cs`): pede a senha nova por prompt interativo sem eco (nunca por argumento, histórico de shell ou log), valida pela mesma `PasswordPolicy` e gera o hash com o mesmo `IPasswordHasher<Usuario>` usados pelo seed, e **revoga na mesma operação** todas as `AuthSessions` ativas dessa conta — sem isso, como o item 1 mostra, a troca de senha sozinha não derruba sessões já autenticadas. O seed (`DbSeeder.SeedAsync`) continua nunca sobrescrevendo a senha de um admin já existente — alterar `SEED_ADMIN_SENHA` no `.env` depois do primeiro startup não tem efeito algum sobre a senha real; esta ferramenta é o jeito de trocar a senha de um admin já existente.
3. **SQL Server (`sa`)**: trocar `SQL_SA_PASSWORD` no `.env` **não** altera a senha já persistida no volume `almirante-sqlserver-data` de um SQL Server existente — é preciso trocar a senha no próprio SQL Server (ex.: `ALTER LOGIN sa WITH PASSWORD = '<nova>'` numa sessão conectada com a senha atual) e só depois atualizar o `.env`/connection string. Nunca recrie o volume/banco só para "resolver" a senha sem backup validado.

Esta entrega corrige e completa o suporte de código a essas rotações (inclusive a configuração para retirar `JWT_KEY_V1` por completo, e a ferramenta de senha do admin); ela **não** executa nem comprova rotação em nenhum ambiente real — isso continua sendo uma ação operacional de quem administra esse ambiente.

**Fora de escopo desta entrega, recomendado para o futuro:** um endpoint de troca de senha autoatendido (autenticado, exigindo a senha atual, revogando as demais sessões) evitaria a necessidade de acesso ao servidor para essa rotação.

## Cabeçalhos de segurança HTTP (#35)

`SecurityHeadersMiddleware` (logo após `UseForwardedHeaders`, antes de Swagger, tratamento de erros, rate limiting, autenticação e autorização) grava via `Response.OnStarting`, portanto também em `401`, `403`, `404`, `429`, `400` de validação e `500`:

- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`.
- CSP da API JSON: `default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`.
- CSP de `/swagger`: `default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`. `unsafe-inline` só em estilos, porque o swagger-ui injeta estilo em runtime (verificado no navegador: sem violações com esta política).
- HSTS `max-age=31536000` (seção `Hsts`), somente fora de Development, somente em requisições HTTPS (considerando `X-Forwarded-Proto` apenas de proxy confiável) e nunca para `localhost`/`127.0.0.1`/`[::1]`. `includeSubDomains` e `preload` ficam desligados até confirmar que todos os subdomínios servem HTTPS. Emitido pelo middleware (e não por `UseHsts`) para sobreviver às respostas de erro.
- O nginx não adiciona esses cabeçalhos às respostas da API (evita duplicidade); só às respostas que ele mesmo gera (`429` do login).

## TLS do SQL Server em produção (#36)

- Development: `Encrypt=True;TrustServerCertificate=True` é permitido — o SQL Server do container (`compose.yaml`) e o do `AlmiranteDbContextFactory` (design-time, `dotnet ef`) usam certificado autoassinado e não há PKI própria do projeto. Controlado por `SQL_TRUST_SERVER_CERTIFICATE` no `.env` (padrão `True` no `.env.example`, documentado como exclusivo de dev).
- Production: a connection string deve usar `Encrypt=True;TrustServerCertificate=False`, com o SQL Server apresentando um certificado confiável (dentro da validade, chave privada protegida no servidor, SAN compatível com o host da connection string, fora do Git). A aplicação não valida esse certificado por conta própria — quem faz isso é o driver (`Microsoft.Data.SqlClient`) ao abrir a conexão.
- `SqlServerConnectionSecurityValidator` (`Infrastructure/SqlServerConnectionSecurityValidator.cs`), registrado via `IValidateOptions<ConnectionStringsOptions>` com `ValidateOnStart()`, falha o startup em `Production` (`IHostEnvironment.IsProduction()`) se `ConnectionStrings:almirante` tiver `TrustServerCertificate=True` ou `Encrypt=False` — mesmo padrão de "falha clara, sem vazar segredo" usado por `JwtOptionsValidator`: a mensagem cita só os nomes das opções, nunca a connection string. Fora de Production, ou sem essa connection string configurada, a validação não faz nada.
- `compose.yaml` não fixa mais `TrustServerCertificate=True`: o valor vem de `SQL_TRUST_SERVER_CERTIFICATE` (padrão `False`, seguro). Um deploy de produção que reutilize esse Compose com `ASPNETCORE_ENVIRONMENT=Production` e essa variável setada como `True` por engano faz a API recusar iniciar, em vez de subir com TLS mal configurado.
- `AlmiranteDbContextFactory` continua restrito a design-time (`dotnet ef`): a senha nele é só um placeholder, não uma credencial real, e `ALMIRANTE_DESIGN_TIME_CONNECTION` permite apontar para outra instância local sem editar o arquivo. Esse caminho não passa pelo validador acima (não usa `ConnectionStrings:almirante`/DI) nem precisa passar — não é usado para servir tráfego.
- Fora de escopo: PKI própria do projeto, desabilitar TLS em dev, versionar certificados/chaves privadas, trocar de SGBD.

## TLS no ponto de entrada do deploy (#50)

O nginx é o único ponto de entrada HTTP/HTTPS externo (ver "Arquitetura com Nginx" no README); a API nunca é exposta diretamente. Dois overlays opt-in do `compose.yaml` cobrem TLS em pontas diferentes — **nunca aplique os dois ao mesmo tempo**:

| | `compose.https.yaml` | `compose.tls.yaml` |
| --- | --- | --- |
| Uso | desenvolvimento local | ambiente publicado (domínio/IP real) |
| Certificado | autoassinado, gerado localmente (`docs/local-login.md`) | real, emitido por uma CA (ex.: Let's Encrypt) ou fornecido pela infra do domínio |
| Onde fica o certificado | `nginx/certs/` (fora do Git, `.gitignore`) | caminho arbitrário no host, fora do repositório, apontado por `TLS_CERT_PATH`/`TLS_KEY_PATH` |
| Porta 80 | continua servindo a API em HTTP (não redireciona) — permite testar sem TLS | só redireciona (`308`) para HTTPS — nunca serve conteúdo |
| Config do nginx | `nginx/nginx.conf` + `nginx/https.conf` (mesmo `server`, dois `listen`) | `nginx/nginx.tls.conf` (dois `server` — um só de redirect, outro TLS) |

### Ativando `compose.tls.yaml`

1. Obtenha um certificado real (fullchain + chave privada) para o domínio/IP público desse ambiente. Como emiti-lo é responsabilidade de quem administra o domínio/infraestrutura (ex.: Certbot/Let's Encrypt apontando para esse host) — está fora do que este repositório pode fazer sozinho.
2. Copie os dois arquivos para um caminho no host **fora do repositório**, com permissão restrita à chave privada (`chmod 600` no Linux).
3. No `.env` fixo dessa máquina (nunca no Git — é o mesmo `.env` que o workflow `backend-deploy.yml` copia a cada deploy), defina:
   - `DEPLOY_MODE=tls` (novo; sem isso o workflow de deploy continua publicando em HTTP simples, mesmo que as variáveis abaixo estejam preenchidas — a seleção do modo deixou de ser implícita)
   - `NGINX_CONF_FILE=nginx.tls.conf`
   - `TLS_CERT_PATH=/caminho/para/fullchain.pem`
   - `TLS_KEY_PATH=/caminho/para/privkey.pem`
   - `API_HOST_PORT=80` (senão a porta do redirect continua sendo a mesma de hoje, ex. `8090` — funciona, mas exige abrir/encaminhar essa porta em vez da 80 padrão)
   - HTTPS pública é sempre 443 (`compose.tls.yaml` não aceita mais porta alternativa — evita um redirecionamento para a porta errada). Se 443 já estiver em uso neste host, este overlay não é utilizável sem uma solução de infraestrutura separada.
4. `docker compose -f compose.yaml -f compose.tls.yaml up -d --build` (o workflow de deploy faz isso automaticamente quando `DEPLOY_MODE=tls`, incluindo `docker compose config --quiet` antes de subir).
5. Valide: `curl -I http://<host>/` deve responder `308` com `Location: https://<host>/`; `curl -fsS https://<host>/health` (sem `-k`/`--insecure`) deve responder `200` validando a cadeia do certificado de verdade — um `308`, um socket aberto ou só a API interna respondendo **não** comprovam a borda HTTPS.

Preservado sem alterações: a API continua só com `expose: "8080"` (nunca publicada diretamente no host), o healthcheck interno API↔nginx continua em HTTP dentro da rede Docker (TLS protege a borda externa, não o tráfego interno do Compose), `ReverseProxy__TrustedNetworkCidr`/`API_TRUSTED_PROXY_CIDR` continuam restritos à subnet do Compose, e o nginx continua enviando `X-Forwarded-Proto: https` somente quando a conexão externa realmente foi HTTPS — o que faz `Request.IsHttps`, os cookies `Secure`, o antiforgery e o HSTS (`SecurityHeadersMiddleware`) funcionarem corretamente atrás do proxy sem exigir HTTPS também entre nginx e API.

O healthcheck do container nginx (`compose.tls.yaml`) usa `http://127.0.0.1/internal-health`, uma rota interna (`nginx/nginx.tls.conf`, restrita a `127.0.0.1`, nunca alcançável de fora) que não depende do redirecionamento — evita o problema de um `wget --spider http://127.0.0.1/health` seguir o `308` e falhar por identidade de certificado (emitido para o domínio público, não para `127.0.0.1`).

### Renovação/recarga do certificado

- **Linux (Compose)**: substitua os arquivos apontados por `TLS_CERT_PATH`/`TLS_KEY_PATH` no host e rode `docker compose -f compose.yaml -f compose.tls.yaml exec nginx nginx -s reload` (não precisa recriar o container). Se o certificado vier do Certbot, configure um `--deploy-hook` que rode esse comando após cada renovação automática.
- **Windows/IIS**: este caminho é loopback-only (`nginx/nginx.windows.conf`, `127.0.0.1:8090`) e não tem TLS por padrão — ver "Windows/IIS" abaixo. Se um dia ganhar um certificado próprio fora do repositório, a recarga é `C:\nginx\nginx.exe -s reload` depois de trocar os arquivos de certificado.
- **Validação após renovar**: `curl -vI https://<host>/health` (confira a data de validade do certificado na saída) ou `openssl s_client -connect <host>:443 -servername <host> </dev/null | openssl x509 -noout -dates`.

### Windows/IIS

O deploy Windows/IIS é loopback-only por design (`nginx/nginx.windows.conf` só escuta `127.0.0.1:8090`) e nunca foi exposto à internet — o ponto de entrada público de verdade é sempre o Linux via Compose. Por isso ele não tem (e não precisa ter) um listener TLS/443 próprio hoje. O workflow (`deploy-windows`) agora verifica, antes de sobrescrever `C:\nginx\conf\almirante.conf`, se a config atual já tem um listener `443`/`ssl` que não veio do repositório — se tiver, o passo aborta em vez de apagar essa configuração, para o caso desse host algum dia ganhar TLS configurado manualmente fora do repositório. Se isso vier a ser necessário: instale o certificado fora do Git, adicione um `server { listen 443 ssl; ... }` equivalente ao de `nginx/nginx.tls.conf` num arquivo próprio para o Windows, e mantenha o guard do workflow ciente dele — não abra esse listener automaticamente a partir do workflow.

**Pendência operacional:** este repositório passa a ter suporte completo a TLS real no ponto de entrada Linux/Compose, mas nenhum certificado real foi emitido nem instalado em nenhum ambiente por esta entrega — isso depende de um domínio/IP público e de quem administra essa infraestrutura.

## Contrato da SPA e CSRF

1. Faça `GET /api/Auth/csrf` com `credentials: "include"`; mantenha `csrfToken` somente em memória.
2. Envie `X-CSRF-TOKEN` e `credentials: "include"` no login. Guarde o access token somente em memória e chame `/api/Auth/Me` com Bearer. Nunca reconstrua o perfil decodificando JWT.
3. Após reload, obtenha novo CSRF, chame `POST /api/Auth/refresh`, então `/Me`. Coordene uma única renovação em andamento (inclusive entre abas) sem Web Storage.
4. **Depois do login**, se o cliente anexa `Authorization: Bearer` em toda requisição, peça um novo `GET /api/Auth/csrf` antes de chamar `refresh`/`logout` com esse header presente: o antiforgery vincula o token ao usuário autenticado no momento da emissão.
5. Trate `401` limpando a autenticação e pedindo novo login; trate `403` como "sem permissão" **sem** encerrar a sessão; trate `429` respeitando `Retry-After`. Não crie loops de refresh nem repita automaticamente escrita que talvez já tenha sido executada.

**Pendência do frontend:** o frontend Angular fica em outro repositório (`rocha06101/projeto-almirante-local`), fora desta branch. No estado inspecionado (somente leitura) ele guarda o token em `localStorage`, não obtém CSRF (o login atual exige), não usa refresh por cookie, não preenche o estado a partir de `/Me` e faz logout em qualquer erro de `/Me` (inclusive `403`). A integração **não** está concluída.

## Implantação

1. Faça backup e aplique as migrations (executadas no startup). Esta entrega adiciona `AddAtualizadoPorUsuarioIdToLancamentos` (coluna nullable + índice + FK `Restrict` para `Usuarios`; não altera nem apaga linhas existentes — lançamentos atualizados antes dela simplesmente mantêm `AtualizadoPorUsuarioId = NULL`). Entregas anteriores corrigiram `20260917120000_UnifyLancamentosFlow`, que não tinha `[DbContext]` e era ignorada pelo EF — bancos que já rodaram a versão anterior receberam a renomeação `Tipo → Finalidade` e a remoção de `MembroNome`/`Moeda`, além de `AddLoginLockout`.
2. Configure chaves e senhas conforme acima.
3. **TLS é obrigatório para autenticar.** Os cookies antiforgery/refresh são `Secure`; em requisição HTTP a API responde `500` em `csrf`/`login`/`refresh`/`logout` (o Compose base escuta HTTP e envia `X-Forwarded-Proto: http`). Para desenvolvimento, use [`compose.https.yaml` e o guia de login local](local-login.md). Num ambiente publicado (domínio/IP real), use `compose.tls.yaml` com um certificado real — ver ["TLS no ponto de entrada do deploy" acima](#tls-no-ponto-de-entrada-do-deploy-50). Em qualquer caso, o proxy só deve repassar `X-Forwarded-Proto`/`X-Forwarded-For` a partir da rede confiável (`API_TRUSTED_PROXY_CIDR`).
4. Data Protection: o Compose persiste as chaves em `almirante-dataprotection`. A imagem agora cria o diretório com dono `app`; um volume **já criado como root** (comum em máquinas Linux/Pop!_OS que rodaram uma versão anterior da imagem — issue #50) e não vazio precisa de `chown` único — descubra a imagem em uso com `docker compose images api` e rode `docker run --rm --user root -v almirante-dataprotection:/d --entrypoint chown <imagem-da-api> -R app:app /d` (comando verificado em volume de teste). Isso corrige a posse **uma única vez**, sem apagar as chaves existentes; não é necessário repetir a cada deploy nem recriar o volume. No IIS as chaves ficam no perfil do app pool; com várias réplicas, compartilhe-as.
5. Garanta configuração e SQL Server compartilhados entre réplicas. Publique backend e frontend coordenadamente: tokens antigos sem `sid` são rejeitados.

## Operação e riscos residuais

Há uma leitura SQL indexada por request autenticado; não há cache que atrase revogação. Access tokens permanecem reutilizáveis enquanto válidos e a sessão ativa; `jti` não impede replay. Os limites por IP (API e nginx) são locais a cada processo. A limpeza remove em lotes famílias cujo prazo absoluto terminou (com margem de uma hora).
