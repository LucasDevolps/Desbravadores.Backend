# Guia de contribuição

Este guia descreve como contribuir com o backend **Almirante**. Detalhes de execução, arquitetura
e configuração estão no [README](README.md); aqui ficam apenas as regras do fluxo de trabalho.

## Pré-requisitos

- .NET 10 SDK;
- Docker e Docker Compose (execução local com Nginx e SQL Server 2022 em contêiner);
- opcionalmente, .NET Aspire para orquestração local.

Siga as seções [Executar com Docker Compose](README.md#executar-com-docker-compose) ou
[Executar com .NET Aspire](README.md#executar-com-net-aspire) do README. Segredos ficam no `.env`
local (a partir do `.env.example`) e **nunca** são versionados.

## Estratégia de branches

```text
main        ← versão principal/estável
  ↑  PR develop → main
develop     ← integração e próxima versão
  ↑  PR
feature/*, fix/*, security/*, docs/*, ci/*, chore/*, refactor/*, test/*
```

Fluxo esperado:

1. Crie a branch de trabalho a partir de `develop` atualizada.
2. Abra um Pull Request da branch de trabalho para `develop`.
3. Depois do merge e da validação em `develop`, abra um Pull Request `develop → main`.

Pontos importantes do funcionamento atual:

- `main` e `develop` são protegidas por rulesets: não aceitam push direto, force push nem
  exclusão, e o merge exige os checks do CI verdes e a branch do PR atualizada com o destino.
  Detalhes em [Proteção de branches](README.md#proteção-de-branches).
- Um push em `develop` que altere o backend ou a infraestrutura dispara o workflow de deploy
  (`backend-deploy.yml`). Trate o merge em `develop` como uma publicação.
- Não inicie trabalho a partir de `main`. PRs direto para `main` devem ser exceção (por exemplo, o
  back-merge `main → develop` descrito no README).

## Nome de branches

Use `<tipo>/<descrição-curta>`, incluindo o número da issue quando houver:

| Prefixo | Uso | Exemplo |
|---|---|---|
| `feature/` | nova funcionalidade | `feature/eventos-issue-56` |
| `fix/` | correção de bug | `fix/issue-50-pendencias-seguranca` |
| `security/` | endurecimento de segurança | `security/issue-50-hardening` |
| `docs/` | documentação | `docs/atualiza-readme-estado-atual` |
| `ci/` | workflows e automação | `ci/publish-windows-e-migrations` |
| `chore/` | manutenção e configuração do repositório | `chore/65-governance` |
| `refactor/` | refatoração sem mudança de comportamento | `refactor/lancamentos-servicos` |
| `test/` | apenas testes | `test/eventos-concorrencia` |

Use letras minúsculas e hífens. Branches antigas com outros formatos continuam válidas; o padrão
acima vale daqui em diante.

## Commits

O projeto usa [Conventional Commits](https://www.conventionalcommits.org/pt-br/):

```text
<tipo>(<escopo opcional>): <descrição no imperativo/presente>
```

| Tipo | Uso |
|---|---|
| `feat` | nova funcionalidade |
| `fix` | correção de bug |
| `security` | correção ou endurecimento de segurança |
| `docs` | documentação |
| `test` | testes |
| `refactor` | refatoração sem mudança de comportamento |
| `perf` | desempenho |
| `build` | build, dependências, Docker |
| `ci` | workflows do GitHub Actions |
| `chore` | manutenção geral |

Exemplos reais do histórico:

```text
feat(eventos): cobrancas por membro, grupos de preco e exclusao auditada (#56)
fix(auditoria): descartar pool antes de fechar conexao com contexto residual
docs(security): identidades SQL da API, rotacao da credencial administrativa e migracao
refactor(lancamentos): tipa enums e separa responsabilidades
```

Escopos comuns: `eventos`, `lancamentos`, `auth`, `security`, `auditoria`, `deploy`, `governance`.
Referencie a issue com `(#N)` quando fizer sentido. A convenção não é validada automaticamente.

## Pull Requests

Todo PR usa o [template](.github/pull_request_template.md) e deve:

- ter escopo claro e, quando aplicável, estar associado a uma issue (`Closes #N`);
- evitar alterações não relacionadas (refatorações oportunistas vão em PR próprio);
- explicar riscos, impacto em banco e impacto de segurança;
- informar os testes executados;
- atualizar README ou `docs/` quando o comportamento documentado mudar;
- passar nos checks obrigatórios do CI;
- não conter segredos, credenciais ou dados pessoais.

Todas as conversations do PR precisam estar resolvidas antes do merge.

## Testes e CI

O CI (`.github/workflows/backend-ci.yml`) roda em todo push e PR para `main` e `develop`, sem
filtro de caminhos, com três checks obrigatórios:

| Check | O que executa |
|---|---|
| `build-and-test` | restore, build Release e testes sem dependências externas (Windows) |
| `sqlserver-integration` | testes `Category=RequiresSqlServer` contra SQL Server 2022 descartável (Linux) |
| `deploy-scripts` | testes dos scripts de deploy, política dos workflows, cobertura, bootstrap SQL, Compose e Nginx |

Depois dos dois jobs de testes .NET, o job `coverage` consolida a cobertura das duas suítes, publica o
relatório e reprova regressões acima da tolerância em relação ao baseline. Não baixe o baseline para
"passar" um PR: a política e como atualizá-lo legitimamente estão em
[`docs/test-coverage.md`](docs/test-coverage.md).

Além do CI, todo push e PR para `main` e `develop` passa pelos workflows de segurança da cadeia de
suprimentos: `codeql.yml` (CodeQL para C#) e `container-security.yml` (scan da imagem da API com Trivy e
SBOM CycloneDX). O scan reprova HIGH/CRITICAL com correção disponível. O Dependabot abre PRs semanais para
`develop`. Política, exceções e como ver os findings estão em
[`docs/supply-chain-security.md`](docs/supply-chain-security.md).

Para reproduzir localmente o check `build-and-test`:

```bash
dotnet restore backend/Almirante.slnx
dotnet build backend/Almirante.slnx --configuration Release --no-restore
dotnet test backend/Almirante.Api.Tests/Almirante.Api.Tests.csproj --configuration Release --no-build --filter "Category!=RequiresDocker&Category!=RequiresSqlServer"
```

Os testes `Category=RequiresSqlServer` exigem a variável `ALMIRANTE_TEST_SQLSERVER`, com uma
conexão administrativa usada pelo harness para criar e remover bancos e logins de teste.
**Use sempre uma instância descartável ou dedicada a testes** (por exemplo, um contêiner
SQL Server local). Nunca aponte essa variável para um banco de produção ou compartilhado. Veja a
seção [Testes](README.md#testes) do README.

Os scripts do check `deploy-scripts` (requerem Bash; o de Nginx também requer Docker):

```bash
bash scripts/tests/deploy-config.test.sh
bash scripts/tests/workflows.test.sh
bash scripts/tests/coverage.test.sh
bash scripts/tests/sql-bootstrap.test.sh
bash scripts/tests/compose-sql-identities.test.sh
bash scripts/tests/nginx.test.sh
```

Não é obrigatório rodar tudo localmente — o CI executa a suíte completa. Rode o que for pertinente
à mudança:

- código da API: build e testes sem dependências externas; se tocar persistência, concorrência ou
  migrations, também os testes `RequiresSqlServer`;
- `scripts/`, `compose*.yaml`, `docs/sql/` ou `.github/workflows/`: os scripts de teste
  correspondentes;
- `nginx/`: `nginx.test.sh`;
- apenas documentação: nenhum teste local é necessário.

## Mudanças de banco e migrations

Migrations são aplicadas automaticamente na inicialização da API, inclusive no deploy. Por isso:

- crie migrations incrementais com o EF Core; não reescreva migrations já aplicadas, salvo em caso
  extremamente justificado e documentado no PR;
- não destrua dados existentes: prefira conversões que preservem registros e falhem de forma
  transacional diante de dados inesperados;
- avalie e teste tanto o `Up` quanto o `Down`;
- cubra a mudança de schema com testes `RequiresSqlServer` quando houver triggers, índices,
  constraints, concorrência ou conversão de dados;
- alterações de permissões SQL devem respeitar o menor privilégio já adotado (a API não usa `sa`;
  veja [`docs/authentication-security.md`](docs/authentication-security.md));
- não coloque dados sensíveis ou credenciais reais em migrations, scripts SQL, logs ou documentação;
- descreva no PR o impacto, a estratégia de rollback e se versões antiga e nova da API podem
  coexistir sobre o schema.

## Mudanças de segurança

Mudanças que envolvem autenticação, autorização, JWT, cookies, CSRF, sessão, credenciais,
permissões SQL, proxy confiável e forwarded headers, TLS, rate limit, auditoria ou segredos devem:

- descrever explicitamente o impacto de segurança no PR;
- incluir testes de regressão quando aplicável;
- seguir o princípio do menor privilégio;
- nunca reduzir uma proteção de forma silenciosa — qualquer relaxamento precisa ser justificado no
  PR e refletido na documentação;
- nunca introduzir segredos no repositório, nem em exemplos, testes ou logs.

Vulnerabilidades não corrigidas seguem a [política de segurança](SECURITY.md) e não devem ser
discutidas em issues ou PRs públicos.

## Issues

Use os templates disponíveis ao abrir uma issue: bug, feature, melhoria de segurança ou dívida
técnica. Não inclua senhas, tokens, cookies, chaves, connection strings ou outros segredos em
issues, comentários ou evidências.
