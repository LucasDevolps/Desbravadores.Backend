# Segurança da cadeia de suprimentos

Automações que protegem dependências, código e a imagem Docker da API (issue #68). Todas rodam em
runners descartáveis do GitHub (`ubuntu-latest`), nunca nos runners self-hosted de deploy, sem segredos
e com o `GITHUB_TOKEN` no menor privilégio.

| Automação | Onde | Quando |
|---|---|---|
| Dependabot (NuGet, GitHub Actions, Docker) | `.github/dependabot.yml` | semanal (segunda, 06:00 BRT) |
| CodeQL para C# | `.github/workflows/codeql.yml`, job `analyze-csharp` | push e PR para `main`/`develop`, semanal |
| Scan da imagem + SBOM | `.github/workflows/container-security.yml`, jobs `image-scan` e `upload-sarif` | push e PR para `main`/`develop`, semanal |

Os workflows não usam filtro de caminhos: toda mudança em C#, no Dockerfile ou em dependências é analisada.

## Dependabot

- **NuGet** em `/backend`: o Dependabot parte de `Almirante.slnx`, atualiza os quatro `.csproj` e também
  `.config/dotnet-tools.json` (ReportGenerator).
- **GitHub Actions** em `/`: atualiza o SHA pinado **e** o comentário de versão juntos, mantendo a política
  de `scripts/tests/workflows.test.sh` (SHA de 40 caracteres).
- **Docker** em `/backend/Almirante.Api`: imagens base `dotnet/sdk` e `dotnet/aspnet`, agrupadas.
- Minor/patch vêm em **um PR agrupado por ecossistema**; majors vêm em PRs individuais. Majors de pacotes
  acoplados ao runtime (`Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore.*`,
  `Microsoft.Extensions.*`) e das imagens `dotnet/*` são ignorados: trocar de .NET 10 para 11 exige mudar
  o `TargetFramework` e as imagens de propósito, em PR próprio.
- Os PRs de version update miram `develop` (`target-branch`). **Merge em `develop` dispara o deploy**:
  revise um PR do Dependabot como qualquer publicação.
- Security updates do Dependabot (quando habilitadas em *Settings → Code security*) sempre miram a branch
  padrão (`main`), por regra do GitHub, e não são bloqueadas nem agrupadas por esta configuração.

## CodeQL

- Linguagem `csharp`, suíte `security-extended`.
- Build **manual** (`dotnet restore`/`dotnet build backend/Almirante.slnx`) com o .NET 10 instalado pelo
  `setup-dotnet`, em vez de `autobuild`: compila exatamente a solução `.slnx` do projeto.
- Resultados na aba **Security → Code scanning** e como anotações na PR (categoria `/language:csharp`).
- Só o job `analyze-csharp` recebe `security-events: write` (publicar o resultado); o workflow fica em
  `contents: read`. Em PR de fork o GitHub já rebaixa o token para leitura.

## Scan da imagem (Trivy)

```text
checkout → docker build (backend/Almirante.Api/Dockerfile, contexto backend/, --pull)
         → trivy image: relatório completo (JSON) → SARIF HIGH/CRITICAL + Job Summary
         → SBOM CycloneDX da mesma imagem → artifact
         → gate: HIGH/CRITICAL com correção disponível reprova o job
```

- Analisa a **imagem final** que o deploy executaria: pacotes do sistema operacional (Ubuntu da imagem
  `aspnet`) e bibliotecas (`.deps.json` da API e do runtime .NET), com `--pkg-types os,library`.
- **Política de severidade:** reprova o CI qualquer finding **HIGH** ou **CRITICAL** que **tenha correção
  publicada** (`--ignore-unfixed --exit-code 1`). Uma correção existente é sempre acionável (atualizar
  pacote ou reconstruir sobre a imagem base corrigida).
- **Sem correção disponível:** não bloqueia, porque não há ação possível no repositório e o CI ficaria
  vermelho de forma permanente. Continua **visível**: entra no SARIF (Code Scanning), no Job Summary e no
  relatório JSON completo. MEDIUM/LOW não bloqueiam e ficam no Job Summary (contagem) e no JSON.
- **Exceções** a um finding corrigível: só em `.trivyignore`, com justificativa, link para a análise e
  data de expiração (`exp:`), para voltarem a bloquear se ninguém revisar. Nunca use `continue-on-error`.
- **Onde ver:** Job Summary do `image-scan`; aba **Security → Code scanning** (categoria
  `container-image-almirante-api`, publicada pelo job `upload-sarif`); artifact `container-scan-<sha>`
  (`trivy-image.json` completo e `trivy-image.sarif`, 14 dias). Em PR de fork o token é somente leitura e
  o `upload-sarif` é pulado; o resumo, o artifact e o gate continuam.
- O job que executa o build da PR tem só `contents: read`. O `upload-sarif` é separado, não faz build e só
  baixa o SARIF do mesmo run para publicá-lo com `security-events: write`.

### Por que o binário do Trivy, e não a action

As tags de `aquasecurity/trivy-action` e `aquasecurity/setup-trivy` foram sequestradas em março de 2026
(advisory [GHSA-69fq-xp46-6x23](https://github.com/aquasecurity/trivy/security/advisories/GHSA-69fq-xp46-6x23)).
O workflow baixa o binário oficial de um **release imutável** do GitHub e confere o **SHA256 fixado** no
workflow antes de executá-lo, o mesmo padrão do Nginx em `backend-deploy.yml`.

**Para atualizar o Trivy** (o Dependabot não cobre esse binário): escolha uma versão com release imutável,
pegue o hash de `trivy_<versão>_Linux-64bit.tar.gz` em `trivy_<versão>_checksums.txt` do release e troque
`TRIVY_VERSION` e `TRIVY_SHA256` juntos em `container-security.yml`.

## SBOM

- Ferramenta: Trivy (mesmo binário verificado do scan), formato **CycloneDX JSON**.
- Artefato descrito: a **imagem final da API** (SO, runtime .NET e pacotes NuGet publicados), não só o
  código-fonte.
- Publicada em todo run como artifact **`sbom-almirante-api-<sha>`** (arquivo
  `almirante-api-<sha>.cdx.json`), com retenção de 90 dias. `<sha>` é o commit analisado (`github.sha`;
  em PR, o merge commit de teste).
- A imagem é construída só a partir de arquivos versionados no checkout: não contém `.env`, segredos,
  certificados ou dados do ambiente de deploy, e a SBOM lista apenas pacotes e versões.
- O job falha se a SBOM sair sem componentes.

## Política verificada automaticamente

`scripts/tests/workflows.test.sh` (check `deploy-scripts`) reprova um PR que, entre outras coisas:

- use `permissions: write-all`, `pull_request_target` ou `security-events: write` no topo de um workflow;
- dê `security-events: write` ao job que executa o build da PR;
- use action sem SHA de 40 caracteres, ou `aquasecurity/trivy-action`/`setup-trivy`;
- remova do scan o gate HIGH/CRITICAL `--ignore-unfixed --exit-code 1`, a análise de SO e bibliotecas, a
  conferência do SHA256 ou a publicação da SBOM;
- use `continue-on-error` nos workflows de segurança;
- deixe de monitorar NuGet, GitHub Actions ou Docker no Dependabot.
