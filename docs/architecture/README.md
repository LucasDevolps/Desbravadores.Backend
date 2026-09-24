# Documentação de arquitetura

Esta pasta descreve a arquitetura do backend Almirante como ela existe no código, e registra as
decisões arquiteturais que levaram a ela. O objetivo é que alguém novo no projeto entenda o que é o
sistema, como as partes se ligam e por que certas escolhas foram feitas, com evidência rastreável
até o repositório.

**Escopo:** este repositório (API, nginx, SQL Server, scripts, Compose, Aspire e workflows). O
frontend fica em outro repositório e aparece só como sistema externo.

**Retrato:** commit `57f24dd` da `main`, levantado em 2026-09-23 (issue #69). Uma mudança posterior
no código pode tornar algum trecho desatualizado; veja
[como manter a documentação atualizada](#como-manter-a-documentação-atualizada).

## Documentos

| Documento | Responde |
| --- | --- |
| [`c4-context.md`](c4-context.md) | Quem usa o sistema e com o que ele se relaciona (C4 nível 1). |
| [`c4-container.md`](c4-container.md) | Quais containers existem, como se comunicam e como variam entre Docker Compose, Windows/IIS e .NET Aspire (C4 nível 2). Inclui observabilidade, inicialização da API e entrega. |
| [`adr/README.md`](adr/README.md) | Índice dos ADRs, regras de criação, numeração e status. |
| [`adr/0000-template.md`](adr/0000-template.md) | Template para novos ADRs. |

Documentação complementar já existente:

- [`docs/authentication-security.md`](../authentication-security.md): autenticação, RBAC, TLS,
  identidades SQL e riscos residuais em detalhe;
- [`docs/local-login.md`](../local-login.md): login local pelo nginx com HTTPS;
- [`docs/test-coverage.md`](../test-coverage.md): cobertura e quality gate;
- [`docs/supply-chain-security.md`](../supply-chain-security.md): Dependabot, CodeQL, Trivy e SBOM.

## Arquitetura em resumo

```text
Frontend (SPA, outro repositório)
        │  HTTPS: JWT Bearer + cookies de refresh e CSRF
        ▼
nginx ── único ponto de entrada: TLS, rate limit de login/refresh, forwarded headers
        │  HTTP na rede interna
        ▼
API ASP.NET Core (.NET 10) ── autenticação, RBAC, usuários, cargos, lançamentos, eventos
        │  TDS com TLS, identidades SQL dedicadas (nunca sa)
        ▼
SQL Server 2022 ── dados, triggers de auditoria, rowversion, índices de idempotência
```

- **Deploy Linux:** Docker Compose com `nginx`, `api`, `sqlserver` e o container efêmero
  `sql-bootstrap`. É a topologia principal.
- **Deploy Windows:** a API no IIS com nginx nativo, acessível só por loopback.
- **Desenvolvimento:** o AppHost do .NET Aspire sobe SQL Server, bootstrap e API, sem nginx, e mostra a
  telemetria no dashboard. O Aspire não faz parte de nenhum deploy.
- **Sem dependências externas de runtime:** a API não chama outros serviços. O SQL Server é o único
  armazenamento de dados de negócio.
- **Observabilidade:** a API é instrumentada com OpenTelemetry. Só exporta telemetria quando
  `OTEL_EXPORTER_OTLP_ENDPOINT` está definido, o que hoje acontece apenas no Aspire.

## Decisões registradas

| ADR | Decisão |
| --- | --- |
| [0001](adr/0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md) | JWT de curta duração com sessões persistidas e refresh token rotativo |
| [0002](adr/0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md) | Nginx como reverse proxy e único ponto de entrada dos deploys |
| [0003](adr/0003-exigir-idempotency-key-nas-criacoes-em-lote.md) | Idempotency-Key persistida nas criações em lote |
| [0004](adr/0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md) | Auditoria de exclusões lógicas por trigger e `SESSION_CONTEXT` |
| [0005](adr/0005-separar-identidades-sql-e-rotacionar-credenciais.md) | Identidades SQL separadas e rotação da credencial de runtime |
| [0006](adr/0006-controlar-concorrencia-com-rowversion.md) | Concorrência otimista com `rowversion` em eventos e sessões |
| [0007](adr/0007-testar-integracao-com-sql-server-real-no-ci.md) | SQL Server real e descartável nos testes do CI |
| [0008](adr/0008-usar-aspire-no-desenvolvimento-e-opentelemetry-na-api.md) | Aspire no desenvolvimento e ServiceDefaults/OpenTelemetry na API |
| [0009](adr/0009-aplicar-migrations-na-inicializacao-da-api.md) | Migrations aplicadas na inicialização da API |
| [0010](adr/0010-publicar-releases-como-imagem-imutavel-no-ghcr.md) | Releases SemVer publicadas como imagem imutável no GHCR |

## Documentação atual e decisão histórica

Os dois tipos de documento têm papéis diferentes:

- **C4 (`c4-*.md`) é documentação atual.** Descreve como o sistema está agora. Quando a arquitetura
  muda, o documento é editado para refletir a mudança, e a versão anterior fica no histórico do Git.
- **ADR (`adr/`) é registro histórico.** Descreve uma decisão, o contexto em que foi tomada e as
  consequências esperadas. Um ADR aceito não é reescrito. Se a decisão muda, um ADR novo a substitui
  (ver [`adr/README.md`](adr/README.md#alterar-ou-substituir-uma-decisão)).

Os ADRs 0001 a 0009 são retroativos: foram escritos depois da implementação, a partir das evidências
do repositório.

## Critério de evidência

Tudo nesta pasta foi conferido contra código, configuração, testes, workflows, documentação, commits ou
PRs. Três categorias são usadas:

- **Confirmado:** há evidência direta, e o texto afirma o fato com link para ela.
- **Inferido:** a conclusão decorre do código, mas não está escrita em lugar algum. O texto começa com
  "Inferido:" ou diz explicitamente que a conclusão é inferida.
- **Desconhecido:** o repositório não permite determinar. Aparece em "Questões em aberto", nunca como
  fato.

Quando a documentação existente diverge do código, estes documentos seguem o código e registram a
divergência abaixo.

## Como criar um ADR

Resumo das regras completas de [`adr/README.md`](adr/README.md):

1. Copie [`adr/0000-template.md`](adr/0000-template.md) para `adr/NNNN-titulo.md`, com o próximo número
   de quatro dígitos. Números nunca são reutilizados.
2. Nome do arquivo em minúsculas, sem acentos, com verbo no infinitivo:
   `0010-usar-algo-para-algo.md`.
3. Status possíveis: `Proposed`, `Accepted`, `Deprecated` e `Superseded by ADR-NNNN`.
4. Inclua a seção "Evidências no repositório" com links relativos e adicione o ADR ao índice.

## Como manter a documentação atualizada

Atualize esta pasta no mesmo PR da mudança quando ela:

- adicionar, remover ou trocar um container, serviço do Compose, recurso do AppHost ou sistema
  externo: atualize [`c4-container.md`](c4-container.md) e, se mudar a fronteira ou os atores,
  [`c4-context.md`](c4-context.md);
- mudar portas, protocolos, redes, identidades SQL ou o destino da telemetria: atualize as tabelas de
  relações e a seção de observabilidade;
- tomar ou reverter uma decisão arquitetural: crie um ADR novo; não edite o conteúdo de um ADR aceito;
- mover ou renomear arquivos citados como evidência: corrija os links (isso não muda a decisão).

Ao atualizar um diagrama, confira cada elemento e cada seta contra o código e a configuração. Se não
houver evidência de que uma relação existe, ela não entra no diagrama.

## Divergências encontradas durante o levantamento

Diferenças entre a documentação existente e o código no commit `57f24dd`. Elas não foram corrigidas
nesta issue, que é só de documentação de arquitetura.

| Onde | O documento diz | O código mostra |
| --- | --- | --- |
| [`README.md`](../../README.md#swagger) (seções "Estado atual" e "Swagger") | Swagger disponível em todos os ambientes | `Program.cs` habilita o Swagger por padrão só em `Development`, ou por `Swagger:Enabled`; `compose.tls.yaml` o desliga |
| [`README.md`](../../README.md#variáveis-de-ambiente) (tabela de variáveis) | variável `TLS_HTTPS_HOST_PORT`, padrão 443 | `compose.tls.yaml` fixa `443:443`, sem variável |
| [`README.md`](../../README.md#cenários-manuais-do-rate-limit-do-nginx) | o rate limit do nginx não é coberto por teste automatizado | `scripts/tests/nginx.test.sh` testa HTTP, HTTPS e `429` no job `deploy-scripts` do CI; só a suíte .NET não o cobre |
| [`docs/authentication-security.md`](../authentication-security.md#impersonate-permissão-de-classe-4) | cita `docs/sql/corrigir-privilegios-usuario-app.sql` | o arquivo não existe; a normalização está em `docs/sql/criar-usuario-admin-app.sql` e em `DbCredentialManager.BuildProvisionSql` |
| comentário em `backend/Almirante.Api/Options/DbCredentialOptions.cs` | AppHost/Aspire roda sem `DbCredentials:AppUser` | `AppHost.cs` define `DbCredentials__AppUser`, então o modelo de identidades fica ativo no Aspire |

## Questões em aberto

O repositório não permite determinar:

- qual `DEPLOY_MODE` (`http` ou `tls`) e qual `ASPNETCORE_ENVIRONMENT` estão em uso em cada ambiente
  publicado, porque esses valores vêm do `.env` de cada máquina;
- onde fica o SQL Server do deploy Windows/IIS e qual identidade a API usa nele;
- como os usuários, além do administrador inicial, são cadastrados nos ambientes, já que a API não
  expõe cadastro de usuários;
- a motivação original para adotar o .NET Aspire e se há destino planejado para a telemetria dos
  ambientes publicados.

Questões específicas de cada decisão estão nos próprios ADRs.
