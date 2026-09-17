# Autenticação, sessões e implantação segura

Esta entrega amplia expressamente a issue #29: além de separar login e perfil, adiciona sessões persistidas, refresh token rotativo e revogação efetiva de access tokens. O fluxo é próprio da aplicação e **não** é apresentado como OAuth 2.0/OIDC.

## Decisões do projeto

- O access token usa HS256 porque emissão e validação permanecem na mesma API. As chaves são bytes aleatórios (mínimo 32), configurados em Base64 e selecionados por `kid`. Somente HS256 e `typ=at+jwt` são aceitos. Para rotacionar, distribua uma nova entrada `Jwt__Keys__<kid>` em todas as réplicas, depois altere `Jwt__ActiveKeyId`; retire a anterior somente após 10 minutos mais o clock skew de 30 segundos, salvo comprometimento, quando deve ser retirada imediatamente.
- O perfil JWT contém somente `sub`, `role`, `iss`, `aud`, `exp`, `iat`, `nbf`, `jti` e `sid`. Isso é o perfil local; a RFC 7519 não exige todas em todo JWT. JWS fornece integridade, não confidencialidade.
- Access tokens duram 10 minutos; sessão absoluta, 7 dias; inatividade medida entre login/refresh, 24 horas. Todos são configuráveis e a expiração do access token é limitada pela sessão.
- O refresh opaco possui 32 bytes CSPRNG. Só seu SHA-256 (`binary(32)`) fica no SQL Server. Isso é apropriado para segredo aleatório de alta entropia, não para senhas humanas.
- O cookie `__Host-almirante-refresh` é host-only, `Secure`, `HttpOnly`, `SameSite=Strict` e `Path=/`. Produção deve terminar TLS; não há fallback inseguro para HTTP.
- Cada request protegido consulta por `sid/sub` a sessão, usuário, cargo, role e versão de segurança. Logout e revogação bloqueiam novas requisições, mas não cancelam uma operação já iniciada.

## Contrato da SPA e CSRF

1. Faça `GET /api/Auth/csrf` com `credentials: "include"`; mantenha `csrfToken` somente em memória.
2. Envie `X-CSRF-TOKEN` e `credentials: "include"` no login. Guarde o access token somente em memória e chame `/api/Auth/Me` com Bearer. Nunca reconstrua o perfil decodificando JWT.
3. Após reload, obtenha novo CSRF, chame `POST /api/Auth/refresh`, então `/Me`. Coordene uma única renovação em andamento (inclusive entre abas) sem Web Storage.
4. **Depois do login**, se o cliente anexa o header `Authorization: Bearer` em toda requisição (interceptor global, por exemplo), peça um novo `GET /api/Auth/csrf` antes de chamar `refresh`/`logout` com esse header presente. O antiforgery do ASP.NET Core vincula o token ao usuário autenticado no momento da emissão (`IClaimUidExtractor`); um token emitido anonimamente (passo 1) é rejeitado com 400 ("meant for a different claims-based user") ao ser validado numa chamada em que o Bearer já está anexado. Chamar `refresh`/`logout` sem o header `Authorization` evita o problema sem precisar desse novo CSRF, mas o padrão mais simples é sempre reobter o CSRF após autenticar.
5. Não crie loops de refresh. Após refresh inválido ou logout, apague o estado. Não repita automaticamente escrita que talvez já tenha sido executada.

Login, refresh e logout exigem antiforgery do ASP.NET Core. CORS usa origens exatas e credenciais. Duas renovações concorrentes são deliberadamente estritas: a reutilização/rejeição revoga a família e exige novo login.

## Implantação e migração

1. Faça backup e aplique `20260917000000_AddAuthenticationSessions` antes de liberar o backend.
2. Gere `openssl rand -base64 32`, injete como `Jwt__Keys__v1` pelo cofre/secret do ambiente e configure `Jwt__ActiveKeyId=v1`. Não coloque chaves em Git, imagem, banco ou logs.
3. Garanta configuração e SQL Server compartilhados entre réplicas. O Nginx deve estar atrás de TLS (ou terminador TLS confiável); seu listener HTTP não fornece TLS sozinho.
4. Publique backend e frontend coordenadamente. Tokens antigos não possuem `sid` e serão rejeitados, exigindo novo login. Rollback do contrato requer rollback coordenado do frontend.

As chaves de Data Protection do antiforgery devem ser persistidas, protegidas e compartilhadas entre réplicas pelo ambiente de hospedagem. Reinícios não podem trocar arbitrariamente a chave. TLS e esse armazenamento são pendências externas, não segredos versionáveis.

## Operação e riscos residuais

Há uma leitura SQL indexada por request autenticado; não há cache que atrase revogação. O rate limit do Nginx é local a cada proxy (login 3/minuto; refresh 30/minuto), não global. A limpeza remove em lotes famílias cujo prazo absoluto terminou (com margem de uma hora); execuções simultâneas são idempotentes. Access tokens permanecem reutilizáveis enquanto válidos e a sessão ativa; `jti` não impede replay.
