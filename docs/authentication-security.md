# Autenticação, autorização e implantação segura

Este documento cobre as issues #29 (login/perfil/JWT), #31 (RBAC), #33 (força bruta), #34 (segredos e política de senha) e #35 (cabeçalhos HTTP). Sessões persistidas, refresh token rotativo e revogação foram adicionados antes (PR #46) e são preservados. O fluxo é próprio da aplicação e **não** é apresentado como OAuth 2.0/OIDC.

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
- Auditoria: `LancamentoOperacao.CriadoPorUsuarioId` e `lancamentos_deletados.UsuarioResponsavelId`/`IpResponsavel` vêm da identidade autenticada e do IP pós-proxy confiável. `PUT` não registra o responsável (não existe coluna para isso; lacuna anterior a esta entrega).

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

Remover um valor do arquivo atual **não** o remove do histórico do Git. Estiveram versionados: a chave JWT de desenvolvimento `dev-only-super-secret-key-change-me-…`, a senha de admin `senha` e a senha SA `Almirante_Dev_2026!` (`.env.example` e AppHost). Se qualquer um deles foi usado fora de uma máquina de desenvolvimento:

1. Gere nova chave, publique-a como nova entrada `Jwt__Keys__<kid>` em todas as réplicas, troque `Jwt__ActiveKeyId`; retire a antiga após 10 min + 30 s (ou imediatamente, se comprometida — todos precisarão entrar de novo).
2. Troque a senha do admin diretamente no banco/fluxo administrativo (o seed não altera admin existente) e revogue as sessões (`UPDATE AuthSessions SET RevokedAtUtc = SYSUTCDATETIME(), RevocationReason = 'rotacao' WHERE RevokedAtUtc IS NULL`).
3. Troque a senha SA do SQL Server e atualize a connection string.

Esta entrega corrige o código; ela não comprova rotação em nenhum ambiente.

## Cabeçalhos de segurança HTTP (#35)

`SecurityHeadersMiddleware` (logo após `UseForwardedHeaders`, antes de Swagger, tratamento de erros, rate limiting, autenticação e autorização) grava via `Response.OnStarting`, portanto também em `401`, `403`, `404`, `429`, `400` de validação e `500`:

- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`.
- CSP da API JSON: `default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`.
- CSP de `/swagger`: `default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`. `unsafe-inline` só em estilos, porque o swagger-ui injeta estilo em runtime (verificado no navegador: sem violações com esta política).
- HSTS `max-age=31536000` (seção `Hsts`), somente fora de Development, somente em requisições HTTPS (considerando `X-Forwarded-Proto` apenas de proxy confiável) e nunca para `localhost`/`127.0.0.1`/`[::1]`. `includeSubDomains` e `preload` ficam desligados até confirmar que todos os subdomínios servem HTTPS. Emitido pelo middleware (e não por `UseHsts`) para sobreviver às respostas de erro.
- O nginx não adiciona esses cabeçalhos às respostas da API (evita duplicidade); só às respostas que ele mesmo gera (`429` do login).

## Contrato da SPA e CSRF

1. Faça `GET /api/Auth/csrf` com `credentials: "include"`; mantenha `csrfToken` somente em memória.
2. Envie `X-CSRF-TOKEN` e `credentials: "include"` no login. Guarde o access token somente em memória e chame `/api/Auth/Me` com Bearer. Nunca reconstrua o perfil decodificando JWT.
3. Após reload, obtenha novo CSRF, chame `POST /api/Auth/refresh`, então `/Me`. Coordene uma única renovação em andamento (inclusive entre abas) sem Web Storage.
4. **Depois do login**, se o cliente anexa `Authorization: Bearer` em toda requisição, peça um novo `GET /api/Auth/csrf` antes de chamar `refresh`/`logout` com esse header presente: o antiforgery vincula o token ao usuário autenticado no momento da emissão.
5. Trate `401` limpando a autenticação e pedindo novo login; trate `403` como "sem permissão" **sem** encerrar a sessão; trate `429` respeitando `Retry-After`. Não crie loops de refresh nem repita automaticamente escrita que talvez já tenha sido executada.

**Pendência do frontend:** o frontend Angular fica em outro repositório (`rocha06101/projeto-almirante-local`), fora desta branch. No estado inspecionado (somente leitura) ele guarda o token em `localStorage`, não obtém CSRF (o login atual exige), não usa refresh por cookie, não preenche o estado a partir de `/Me` e faz logout em qualquer erro de `/Me` (inclusive `403`). A integração **não** está concluída.

## Implantação

1. Faça backup e aplique as migrations (executadas no startup). Esta entrega corrige `20260917120000_UnifyLancamentosFlow`, que não tinha `[DbContext]` e era ignorada pelo EF — bancos que já rodaram a versão anterior receberão agora a renomeação `Tipo → Finalidade` e a remoção de `MembroNome`/`Moeda`, além de `AddLoginLockout`.
2. Configure chaves e senhas conforme acima.
3. **TLS é obrigatório para autenticar.** Os cookies antiforgery/refresh são `Secure`; em requisição HTTP a API responde `500` em `csrf`/`login`/`refresh`/`logout` (verificado no Compose atual, cujo nginx só escuta HTTP e envia `X-Forwarded-Proto: http`). Termine TLS no nginx ou em um proxy confiável que encaminhe `X-Forwarded-Proto: https` até a API — ajustando o nginx para repassar esse valor apenas desse proxy.
4. Data Protection: o Compose persiste as chaves em `almirante-dataprotection`. A imagem agora cria o diretório com dono `app`; um volume **já criado como root** e não vazio precisa de `chown` único (`docker run --rm --user root -v almirante-dataprotection:/d --entrypoint chown <imagem-da-api> -R app:app /d`; comando verificado em volume de teste). No IIS as chaves ficam no perfil do app pool; com várias réplicas, compartilhe-as.
5. Garanta configuração e SQL Server compartilhados entre réplicas. Publique backend e frontend coordenadamente: tokens antigos sem `sid` são rejeitados.

## Operação e riscos residuais

Há uma leitura SQL indexada por request autenticado; não há cache que atrase revogação. Access tokens permanecem reutilizáveis enquanto válidos e a sessão ativa; `jti` não impede replay. Os limites por IP (API e nginx) são locais a cada processo. A limpeza remove em lotes famílias cujo prazo absoluto terminou (com margem de uma hora).
