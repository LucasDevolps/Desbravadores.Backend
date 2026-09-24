# ADR-0001: Usar JWT de curta duração com sessões persistidas e refresh token rotativo

## Status

Accepted (registro retroativo em 2026-09-23). Introduzido no PR #46; ajustado nas issues #29 e #50.

## Contexto

A API é consumida por um frontend SPA que roda no navegador e fica em outro repositório
([`docs/authentication-security.md`](../../authentication-security.md#contrato-da-spa-e-csrf)). O
CORS da API permite credenciais para as origens configuradas em `Cors:AllowedOrigins`.

O que um usuário pode fazer depende do cargo dele (`Cargo.Role`), e esse cargo pode mudar ou ser
desativado. A senha de um usuário pode ser redefinida pela CLI `reset-admin-password`. O acesso
precisa refletir essas mudanças sem esperar a expiração de um token já emitido.

Antes do PR #46, o login emitia apenas um JWT stateless. A descrição do PR declara como motivação
substituir esse login por um modelo baseado em sessão e revogável, com refresh token rotativo,
proteção CSRF e rotação de chave.

## Decisão

A API autentica com um access token JWT de curta duração, emitido e validado por ela mesma, ligado a
uma sessão persistida no SQL Server e renovado por um refresh token opaco e rotativo em cookie.

**Access token**

- JWT assinado com HS256. A chave é escolhida pelo `kid` em `Jwt:Keys` e novos tokens usam
  `Jwt:ActiveKeyId`. Várias chaves podem coexistir, o que permite rotacioná-las.
- Claims: `sub`, `role`, `sid`, `jti`, `iat`, `nbf`, `exp`, `iss` e `aud`, com `typ=at+jwt`. Nome e
  e-mail não entram no token.
- A validade é de `Jwt:AccessTokenMinutes` (padrão 10, faixa 1–30), limitada ao fim absoluto da
  sessão.
- A validação aceita somente HS256 e `typ=at+jwt` e exige emissor, audiência, assinatura e
  expiração, com tolerância de relógio de 30 s e tamanho máximo de 8 KB.
- A startup falha se a chave estiver ausente, for placeholder, não for Base64 válido, tiver menos
  de 32 bytes decodificados ou for trivial (`JwtOptionsValidator`).

**Sessão e validação por requisição**

- O login cria uma linha em `AuthSessions` com fim absoluto em `Jwt:AbsoluteSessionDays` (padrão 7)
  e grava o `SecurityVersion` do usuário.
- Em toda requisição autenticada, `OnTokenValidated` consulta o banco. O token só é aceito se a
  sessão do `sid` pertencer ao `sub`, não estiver revogada nem expirada, o `SecurityVersion` ainda
  for o do usuário, o cargo estiver ativo e a role do token for a role atual do cargo.
- Um novo login não revoga as sessões anteriores do mesmo usuário. Inferido: não há limite de
  sessões simultâneas, porque `AuthService.LoginAsync` só cria uma sessão nova.

**Refresh token**

- Valor aleatório de 32 bytes, entregue no cookie `__Host-almirante-refresh` (`HttpOnly`, `Secure`,
  `SameSite=Strict`, `Path=/`). O banco guarda apenas o hash SHA-256 (`RefreshTokens.TokenHash`,
  `binary(32)`, índice único).
- Cada `POST /api/Auth/refresh` consome o token atual e emite outro (`ConsumedAtUtc`,
  `ReplacedByTokenId`).
- A renovação falha se a sessão estiver inativa há mais de `Jwt:RefreshInactivityHours` (padrão 24),
  expirada, revogada, com cargo inativo ou com `SecurityVersion` diferente.
- Reapresentar um refresh token já consumido revoga a sessão (`refresh-token-reuse`). Duas
  renovações concorrentes da mesma sessão também a revogam (`concurrent-refresh`), detectadas pelo
  `rowversion` das tabelas de sessão (ver [ADR-0006](0006-controlar-concorrencia-com-rowversion.md)).

**Revogação e ciclo de vida**

- `POST /api/Auth/logout` revoga a sessão identificada pelo cookie de refresh ou, na falta dele, pelo
  `sid` do access token.
- A CLI `reset-admin-password` grava o hash novo, incrementa `SecurityVersion` e revoga todas as
  sessões ativas do usuário no mesmo `SaveChanges`.
- `Usuario.SecurityVersion` é token de concorrência. Um login lido antes de um reset concorrente
  falha ao persistir, e nenhuma sessão baseada na senha anterior sobrevive.
- `AuthSessionCleanupService` roda a cada 6 horas. Ele apaga, em lotes de 500, as sessões cujo fim
  absoluto passou há mais de uma hora; os refresh tokens saem em cascata.

**CSRF**

- `login`, `refresh` e `logout` exigem antiforgery: cookie `__Host-almirante-csrf` mais o header
  `X-CSRF-TOKEN`, obtido em `GET /api/Auth/csrf`.
- As chaves do Data Protection ficam em `DataProtection:KeysPath` quando configurado. No Compose, esse
  caminho é o volume `almirante-dataprotection`.

**Fora do escopo desta decisão**

- O fluxo é próprio da aplicação e não segue OAuth 2.0/OIDC.
- A proteção do login contra força bruta (rate limit por IP e lockout por conta) complementa este
  modelo e está descrita em
  [`docs/authentication-security.md`](../../authentication-security.md#proteção-contra-força-bruta-33).
  O rate limit do nginx faz parte do [ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md).

## Consequências

### Positivas

- Logout, reset de senha, desativação do cargo e troca de role passam a valer na requisição seguinte,
  sem esperar a expiração do access token.
- O refresh token não fica acessível a JavaScript e não é armazenado em texto claro no banco.
- O reuso de um refresh token já consumido encerra a sessão inteira, o que limita o uso de um token
  roubado.
- A chave de assinatura pode ser trocada sem invalidar tokens já emitidos, porque a validação
  resolve a chave pelo `kid`.
- O token carrega o mínimo de dados pessoais: um identificador e a role.

### Negativas / trade-offs

- Cada requisição autenticada faz uma leitura indexada no SQL Server. A autenticação passa a depender
  da disponibilidade do banco.
- A autenticação exige HTTPS entre o cliente e o proxy: os cookies são `Secure`, e em HTTP puro
  `csrf`, `login`, `refresh` e `logout` respondem `500`
  ([`docs/authentication-security.md`](../../authentication-security.md#implantação)).
- Um access token continua utilizável até expirar enquanto a sessão estiver ativa, e o `jti` não
  impede replay dentro dessa janela.
- Duas abas que renovam ao mesmo tempo revogam a própria sessão. O contrato da SPA pede que o cliente
  coordene uma única renovação.
- Trocar a chave JWT sozinha não encerra sessões: o refresh token é opaco e não depende da chave. Para
  forçar novo login é preciso revogar as sessões.
- `AuthSessions` e `RefreshTokens` acumulam estado. A limpeza depende do serviço em segundo plano e da
  permissão de `DELETE` concedida à identidade de runtime apenas em `AuthSessions`
  ([ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md)).
- HS256 usa chave simétrica. Inferido: nenhum outro serviço consegue validar os tokens sem receber o
  segredo de assinatura. Isso só funciona porque a própria API emite e valida.

## Alternativas consideradas

- **JWT apenas stateless** (estado anterior ao PR #46). O PR o substituiu para permitir revogação,
  refresh rotativo, CSRF e rotação de chave.

Outras alternativas avaliadas não foram encontradas no repositório.

## Evidências no repositório

- [`Program.cs`](../../../backend/Almirante.Api/Program.cs): `AddJwtBearer`,
  `TokenValidationParameters`, `OnTokenValidated`, `AddAntiforgery`, `AddDataProtection`,
  `AddHostedService<AuthSessionCleanupService>`.
- [`AuthController.cs`](../../../backend/Almirante.Api/Controllers/AuthController.cs): rotas `csrf`,
  `login`, `refresh`, `logout` e `Me`, e as opções do cookie de refresh.
- [`AuthService.cs`](../../../backend/Almirante.Api/Services/AuthService.cs): `LoginAsync`,
  `RefreshAsync`, `LogoutAsync`.
- [`JwtTokenService.cs`](../../../backend/Almirante.Api/Security/JwtTokenService.cs): `GenerateToken`,
  `JwtKeySet`, `JwtOptionsValidator`.
- [`JwtOptions.cs`](../../../backend/Almirante.Api/Options/JwtOptions.cs): limites de duração.
- Entidades [`AuthSession.cs`](../../../backend/Almirante.Api/Entities/AuthSession.cs),
  [`RefreshToken.cs`](../../../backend/Almirante.Api/Entities/RefreshToken.cs) e
  [`Usuario.cs`](../../../backend/Almirante.Api/Entities/Usuario.cs) (`SecurityVersion`).
- Mapeamento em [`AlmiranteDbContext.cs`](../../../backend/Almirante.Api/Data/AlmiranteDbContext.cs) e
  migration [`20260917000000_AddAuthenticationSessions.cs`](../../../backend/Almirante.Api/Data/Migrations/20260917000000_AddAuthenticationSessions.cs).
- [`AuthSessionCleanupService.cs`](../../../backend/Almirante.Api/Services/AuthSessionCleanupService.cs).
- [`AdminPasswordResetCli.cs`](../../../backend/Almirante.Api/Cli/AdminPasswordResetCli.cs): revogação
  das sessões no reset.
- Testes: [`AuthTests.cs`](../../../backend/Almirante.Api.Tests/AuthTests.cs)
  (`Logout_RevogaSessao_EBearerAnteriorDeixaDeAutorizar`,
  `Login_ERefresh_ExpoemSomenteEnvelopeToken_EJwtMinimo`),
  [`SecurityRegressionTests.cs`](../../../backend/Almirante.Api.Tests/SecurityRegressionTests.cs)
  (`ResetConcluidoNoMeioDoLogin_NaoDeixaSessaoUtilizavel`,
  `Reset_IncrementaSecurityVersion_InvalidandoAccessTokenAnterior`) e
  [`AdminPasswordResetCliTests.cs`](../../../backend/Almirante.Api.Tests/AdminPasswordResetCliTests.cs).
- Configuração: [`compose.yaml`](../../../compose.yaml) (`Jwt__*`, `DataProtection__KeysPath`) e
  [`.env.example`](../../../.env.example).
- Documentação: [`docs/authentication-security.md`](../../authentication-security.md) e
  [`docs/local-login.md`](../../local-login.md).
- Histórico: PR #46 (commit `3d4fe16`).

## Questões em aberto

- Não foi encontrado teste automatizado para a revogação por reuso de refresh token
  (`refresh-token-reuse`) nem para a revogação por renovação concorrente (`concurrent-refresh`). Os
  testes existentes cobrem o refresh bem-sucedido (`AuthTests`) e a recusa depois do reset de senha
  (`AdminPasswordResetCliTests`).

## Decisões relacionadas

- [ADR-0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md): TLS e rate limit de
  login/refresh no proxy.
- [ADR-0006](0006-controlar-concorrencia-com-rowversion.md): concorrência na renovação da sessão.
