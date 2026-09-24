# Processo de release

Como o backend Almirante é versionado, como uma release é criada e como uma versão publicada é usada em
deploy e em rollback (issue #70). A decisão está registrada no
[ADR-0010](architecture/adr/0010-publicar-releases-como-imagem-imutavel-no-ghcr.md).

Resumo:

- Versões seguem [Semantic Versioning](https://semver.org/lang/pt-BR/) e são tags Git `vMAJOR.MINOR.PATCH`
  criadas a partir de `main`.
- O push da tag dispara [`.github/workflows/release.yml`](../.github/workflows/release.yml). O workflow
  constrói **uma** imagem da API, aplica o gate de vulnerabilidades e publica essa imagem no GHCR. Em
  seguida cria a GitHub Release.
- A imagem fica em `ghcr.io/lucasdevolps/almirante-api` com duas tags, `vX.Y.Z` e `sha-<commit>`. O
  digest `sha256:...` é registrado na release. Não existe tag `latest`.
- Merge em `main` **não** cria versão: só a tag cria.

## Rastreabilidade

Uma release liga cinco identidades, e cada uma aponta para a seguinte:

```text
v0.2.0 (Git tag, anotada)
   ↓  git rev-parse "v0.2.0^{commit}"
commit 3f1c…e9a0 (40 caracteres, contido em main)
   ↓  mesma execução do release.yml
GitHub Release v0.2.0 ──────────────── tabela "Artefato": commit, imagens e digest
   ↓
ghcr.io/lucasdevolps/almirante-api:v0.2.0
ghcr.io/lucasdevolps/almirante-api:sha-3f1c…e9a0
   ↓  as duas tags têm o mesmo manifest (o workflow confere)
sha256:<digest>
```

A imagem também leva os rótulos OCI `org.opencontainers.image.version` (a tag),
`org.opencontainers.image.revision` (o commit) e `org.opencontainers.image.source` (o repositório). Assim,
dá para descobrir de onde veio uma imagem já baixada:

```bash
docker image inspect ghcr.io/lucasdevolps/almirante-api:v0.2.0 \
  --format '{{index .Config.Labels "org.opencontainers.image.version"}} {{index .Config.Labels "org.opencontainers.image.revision"}}'
```

## Versionamento (SemVer)

Formato da tag: `vMAJOR.MINOR.PATCH`. São três números sem zeros à esquerda e com o prefixo `v`. O
workflow valida a tag com a expressão `SEMVER_REGEX` de `release.yml`, que o
`scripts/tests/workflows.test.sh` testa com exemplos válidos e inválidos:

| Aceita | Recusa |
| --- | --- |
| `v0.1.0`, `v0.1.1`, `v0.2.0`, `v1.0.0`, `v1.2.3` | `latest`, `teste`, `v1`, `v1.2`, `foo-1.0`, `1.2.3`, `v01.2.3`, `v1.0.0-rc.1`, `v1.0.0+build` |

Pré-releases (`-rc.1`) e metadados de build (`+...`) ficam de fora de propósito. Hoje não há ambiente de
homologação que consuma candidatas, e cada formato novo exigiria regra própria de ordenação e de "Latest"
na página de releases. Se isso mudar, altere `SEMVER_REGEX`, os exemplos do teste e este documento no
mesmo PR.

Quando incrementar:

| Parte | Quando | Exemplo |
| --- | --- | --- |
| PATCH | correção compatível, sem funcionalidade nova (bug, segurança, dependência, ajuste de configuração) | `v0.1.0 → v0.1.1` |
| MINOR | funcionalidade nova compatível (endpoint, campo opcional, regra nova que não quebra clientes) | `v0.1.1 → v0.2.0` |
| MAJOR | mudança incompatível, quando o projeto estiver em `1.x+` (remover ou renomear endpoint/campo, mudar contrato de autenticação, exigir configuração nova sem padrão) | `v1.4.2 → v2.0.0` |

**Fase `0.x.y` (MVP).** Pelo SemVer, `0.x` é desenvolvimento inicial e a API pública ainda não é
considerada estável. Enquanto o projeto estiver em `0.x`:

- uma mudança incompatível incrementa o **MINOR** (`v0.3.4 → v0.4.0`), e as notas da release devem dizer
  claramente o que quebrou;
- correções compatíveis continuam no PATCH;
- `v1.0.0` é uma decisão consciente do mantenedor: marca o contrato da API como estável. A partir daí,
  quebra de compatibilidade só acontece em MAJOR.

Mudança de schema (migration) não define sozinha o incremento. O que conta é o efeito para quem usa a
API e para quem opera o deploy. Uma migration que impede voltar para a versão anterior deve estar nas
notas da release (ver [Banco e migrations](#banco-e-migrations)).

## Do desenvolvimento à release

```text
feature/*, fix/*, security/*, docs/*, ci/*, chore/* ...
   ↓ PR
develop ─── push dispara backend-deploy.yml (deploy contínuo, build local, sem versão)
   ↓ PR develop → main
main ────── CI, CodeQL e container-security em cada push
   ↓ tag vX.Y.Z criada pelo mantenedor (git tag -a)
release.yml ─→ imagem no GHCR + GitHub Release
```

`develop` continua sendo o ambiente de integração com deploy contínuo e não tem versão. Release é um
passo separado e manual, sempre a partir de um commit que já está em `main`.

## Criar uma release

Pré-requisitos: o PR `develop → main` foi mergeado e os workflows de `main` (CI, CodeQL, Container
Security) estão verdes no commit que vai virar release.

```bash
git switch main
git pull --ff-only origin main
git log --oneline -1
git tag -a v0.1.0 -m "Release v0.1.0"
git push origin v0.1.0
```

- Use **tag anotada** (`-a`): ela guarda autor, data e mensagem da versão. O workflow também aceita tag
  lightweight, porque resolve as duas com `^{commit}`, mas o padrão do projeto é a anotada.
- Faça `git push origin <tag>`, só da tag. `git push --tags` enviaria também qualquer tag local antiga.
- Uma tag fora do formato não publica nada: o job `validate` falha e explica o motivo. Para limpar,
  apague a tag inválida (`git push --delete origin <tag>`). Isso só vale para uma tag **que não gerou
  release**.

## O que o workflow faz

`release.yml` roda somente em `push` de tags `v*.*.*`, sempre em runners descartáveis `ubuntu-latest`. Não
tem gatilho de PR, não usa os runners self-hosted de deploy e não acessa `.env`, SQL Server, chaves JWT,
certificados ou qualquer secret. A imagem é construída só com arquivos versionados.

| Job | Permissões do `GITHUB_TOKEN` | O que faz |
| --- | --- | --- |
| `validate` | `contents: read` | Confere que o ref é uma tag no formato SemVer e calcula o nome da imagem em minúsculas. Resolve o commit da tag (`^{commit}`) e exige que ele seja igual ao HEAD do checkout e ao SHA do evento. Exige que o commit esteja na história de `origin/main` (`git merge-base --is-ancestor`, com histórico completo). Recusa a versão se a GitHub Release já existir. |
| `build-scan` | `contents: read` | Faz checkout do **commit** resolvido. Constrói a imagem com o mesmo Dockerfile e contexto do `container-security.yml` (`backend/Almirante.Api/Dockerfile`, `backend/`, `--pull`) e grava os rótulos OCI. Aplica o mesmo gate do Trivy (HIGH/CRITICAL com correção reprova) e gera a SBOM CycloneDX. Exporta a imagem com `docker save`. |
| `publish` | `packages: write` | Não faz checkout nem build. Carrega a imagem escaneada (`docker load`) e confere o ID e os rótulos. Faz login no `ghcr.io` com o `GITHUB_TOKEN` via stdin e recusa publicar se `vX.Y.Z` ou `sha-<commit>` já existirem. Envia as duas tags e confere que as duas têm o mesmo digest. |
| `github-release` | `contents: write` | Não faz checkout. Cria a GitHub Release da tag existente (`gh release create --verify-tag`), com a tabela de artefato (versão, data, commit, imagens, digest, link do run) seguida das notas geradas pelo GitHub. Anexa a SBOM e escreve o Job Summary. |

A ordem reduz estados parciais:

- falha em `validate` ou `build-scan`: nada foi publicado;
- falha em `publish` antes do push: nada foi publicado;
- falha em `github-release`: a imagem existe e a release não. Use **Re-run failed jobs** no mesmo run,
  que só reexecuta `github-release` com o mesmo digest. Não crie uma tag nova para "consertar" isso.

O workflow não apaga nada do registry nem da página de releases.

### Release notes

As notas combinam duas partes:

1. a tabela **Artefato**, escrita pelo workflow, com o aviso sobre migrations;
2. as notas automáticas do GitHub (PRs mergeados desde a release anterior, com autores, links e o
   "Full Changelog"), agrupadas por label conforme [`.github/release.yml`](../.github/release.yml).

As categorias dependem das labels dos PRs (`enhancement`, `bug`, `security`, `documentation`,
`dependencies`...). PR sem label aparece em "Outras alterações". Antes ou depois da publicação, o
mantenedor pode editar a release para destacar mudanças de segurança, quebras de compatibilidade (em
`0.x`) e notas de deploy ou migrations. A tabela **Artefato** não deve ser alterada.

## Identidades da imagem

| Referência | Exemplo | Uso |
| --- | --- | --- |
| Tag SemVer | `ghcr.io/lucasdevolps/almirante-api:v0.2.0` | leitura humana, escolher uma versão |
| Tag do commit | `ghcr.io/lucasdevolps/almirante-api:sha-<40 hex>` | ligar a imagem ao commit sem consultar a release |
| Digest | `ghcr.io/lucasdevolps/almirante-api@sha256:<64 hex>` | **deploy e rollback**: prova exatamente qual imagem roda |

- O nome é `ghcr.io/<owner em minúsculas>/almirante-api`. O owner `LucasDevolps` vira `lucasdevolps`,
  porque referências de imagem não aceitam maiúsculas. `almirante-api` é o mesmo nome que o
  `container-security.yml` e o Compose (`almirante-api`) já usam.
- A tag do commit usa o SHA **completo**. SHA curto pode ficar ambíguo com o tempo.
- **Não existe `latest`** nem outra tag móvel. Deploy e rollback devem apontar para uma versão ou, melhor,
  para um digest. O workflow e o teste de política impedem que `latest` seja publicado.

## Imutabilidade e versão repetida

Uma versão publicada não muda de conteúdo:

- a tag Git não pode ser reenviada com outro commit sem ser apagada antes;
- o job `validate` recusa uma versão que já tem GitHub Release;
- o job `publish` recusa uma versão cuja tag `vX.Y.Z` ou `sha-<commit>` já exista no GHCR. Nenhuma tag de
  imagem é sobrescrita;
- o digest registrado na release identifica o manifest de forma criptográfica. Mesmo que uma tag fosse
  movida manualmente no registry, `@sha256:...` continuaria apontando para a imagem original.

Consequência: duas versões **no mesmo commit** (ex.: `v0.2.0` e `v0.2.1` sem mudança de código) não são
suportadas, porque `sha-<commit>` já existiria. Uma versão nova precisa de pelo menos um commit novo em
`main`.

Para corrigir uma release com problema, publique uma versão nova (PATCH). Não apague nem recrie tags,
releases ou imagens já publicadas.

## Como conferir uma release

1. **Actions → Release**: o run da tag, com o Job Summary (tag, commit, imagens, digest, link da release).
2. **Releases**: a release `vX.Y.Z`, com a tabela **Artefato** e a SBOM anexada.
3. **Packages → almirante-api** (perfil do owner ou lateral do repositório): as tags `vX.Y.Z` e
   `sha-<commit>` com o mesmo digest.
4. Pela linha de comando:

```bash
git rev-parse "v0.2.0^{commit}"
gh release view v0.2.0
docker buildx imagetools inspect ghcr.io/lucasdevolps/almirante-api:v0.2.0
```

## Build once, deploy many

A imagem da release é o artefato que se promove entre ambientes. Todo ambiente que roda a versão
`vX.Y.Z` roda o **mesmo** digest, sem rebuild no servidor.

```text
release.yml: build → scan → push (uma vez)
                 ↓
ghcr.io/lucasdevolps/almirante-api@sha256:<digest>
   ├── ambiente A
   ├── ambiente B
   └── rollback
```

Para rodar uma release com Docker Compose, use o overlay
[`compose.release.yaml`](../compose.release.yaml). Ele troca o `build:` da API pela imagem publicada e
mantém o resto do `compose.yaml` como está: bootstrap SQL, migrations na inicialização, healthcheck,
volume do Data Protection, hardening e nginx. Também funciona com `compose.tls.yaml`. No `.env` do
ambiente:

```dotenv
ALMIRANTE_API_IMAGE=ghcr.io/lucasdevolps/almirante-api@sha256:<digest-da-release>
```

```bash
docker compose -f compose.yaml -f compose.release.yaml pull api
docker compose -f compose.yaml -f compose.release.yaml up -d --no-build
curl -fsS http://localhost:8090/health
```

Com TLS: `-f compose.yaml -f compose.tls.yaml -f compose.release.yaml`, e o `/health` passa a ser
conferido pelo nome público em HTTPS, como no deploy. O overlay exige Docker Compose 2.24.4 ou superior
(usa `!reset`) e recusa subir sem `ALMIRANTE_API_IMAGE`.

O que **não** muda com esta issue:

- **Desenvolvimento local** continua com `docker compose up --build`: sem o overlay, a API é construída do
  Dockerfile.
- **Deploy contínuo de `develop`** (`backend-deploy.yml`, job `deploy-linux`) continua construindo a
  imagem na máquina a cada push. Um commit de `develop` não tem versão SemVer, então não existe imagem de
  release para ele. Misturar os dois fluxos na mesma máquina não funciona: o próximo push em `develop`
  reconstrói e substitui a API. Um ambiente que roda releases deve ser separado do ambiente de integração,
  ou o deploy dele deve ser feito manualmente com o overlay. Automatizar o deploy de releases por digest
  (por exemplo, um job disparado pela release) fica para uma issue própria.
- **Deploy Windows/IIS** (`deploy-windows`) usa `dotnet publish` e não usa Docker. A imagem do GHCR serve
  apenas ao fluxo containerizado, e o IIS continua independente.

## Rollback

Rollback de aplicação é voltar a rodar a **imagem de uma release anterior**, por digest. Não envolve
rebuild nem revert de código.

1. **Encontrar a versão anterior.** Em **Releases** ou com `gh release list`, escolha a última versão
   conhecida como boa (ex.: `v0.1.3`).
2. **Pegar o digest.** Ele está na tabela **Artefato** da release (`gh release view v0.1.3`). Também dá
   para consultar:

   ```bash
   docker buildx imagetools inspect ghcr.io/lucasdevolps/almirante-api:v0.1.3 --format '{{json .Manifest}}'
   ```

   O `digest` do resultado deve ser igual ao da release.
3. **Verificar o banco antes de voltar** (ver [Banco e migrations](#banco-e-migrations)). Se a versão
   atual aplicou migrations que a anterior não conhece, confirme que a anterior funciona com o schema
   novo antes de continuar.
4. **Selecionar a imagem.** No `.env` do ambiente:

   ```dotenv
   ALMIRANTE_API_IMAGE=ghcr.io/lucasdevolps/almirante-api@sha256:<digest-da-v0.1.3>
   ```

5. **Subir e validar:**

   ```bash
   docker compose -f compose.yaml -f compose.release.yaml pull api
   docker compose -f compose.yaml -f compose.release.yaml up -d --no-build
   curl -fsS http://localhost:8090/health
   docker compose -f compose.yaml -f compose.release.yaml logs --tail=100 api
   ```

   Confirme também qual imagem está rodando:
   `docker inspect almirante-api --format '{{.Image}} {{index .Config.Labels "org.opencontainers.image.version"}}'`.

6. **Voltar para a versão seguinte** depois de corrigido o problema: repita os passos 4 e 5 com o digest
   da versão mais nova (ou de uma nova versão PATCH com a correção).

Não é preciso autenticar no GHCR se o package for público. Se for privado, faça `docker login ghcr.io` no
host com um token de leitura (`read:packages`) do próprio operador e não versione esse token.

## Banco e migrations

**Rollback da imagem ≠ rollback do schema.** A API aplica as migrations pendentes na inicialização
([ADR-0009](architecture/adr/0009-aplicar-migrations-na-inicializacao-da-api.md)), mas não desfaz nenhuma
quando uma versão mais antiga sobe. Voltar a imagem mantém o banco no schema da versão mais nova.

- Nenhum workflow ou script deste repositório reverte migrations, e nenhum deve reverter
  automaticamente. **Não** rode `dotnet ef database update <migration-antiga>`, o `Down` de uma
  migration ou qualquer equivalente em produção como parte de rollback.
- Antes de voltar para uma versão anterior, compare as migrations das duas versões
  (`backend/Almirante.Api/Data/Migrations`):

  ```bash
  git diff --stat v0.1.3 v0.2.0 -- backend/Almirante.Api/Data/Migrations
  ```

  - **Sem migrations novas:** o rollback da imagem é seguro do ponto de vista do schema.
  - **Com migrations aditivas** (tabela ou coluna nova que aceita nulo ou tem padrão): em geral a versão
    antiga continua funcionando, porque ignora o que não conhece. Confirme com o `/health` e com um teste
    funcional.
  - **Com migrations que removem, renomeiam ou tornam obrigatórias colunas ou constraints:** a versão
    antiga pode falhar em runtime. Não faça rollback só da imagem. Avalie uma correção para a frente
    (nova versão PATCH) ou um plano de banco feito à parte, com backup verificado e janela de
    manutenção. Essa decisão é do operador, não é automática.
- O `CONTRIBUTING.md` já pede que o PR de uma migration diga se as versões antiga e nova da API podem
  coexistir sobre o schema. Leve essa informação para as notas da release quando a resposta for "não".
- Backup do volume `almirante-sqlserver-data` antes de uma release com migration relevante é uma boa
  prática operacional. O processo de release não faz esse backup.

## Configuração manual no GitHub

O workflow não configura estes itens. Faça depois do merge, uma vez:

1. **Visibilidade do package.** O primeiro push cria o package `almirante-api` na conta
   `LucasDevolps`, ligado ao repositório pelo rótulo `org.opencontainers.image.source`. Package novo
   nasce **privado**. Para permitir `docker pull` sem login (repositório público), abra
   **Packages → almirante-api → Package settings → Change visibility → Public**. Nessa mesma página,
   confira em **Manage Actions access** que o repositório `Desbravadores.Backend` tem acesso de escrita.
   Ele é concedido automaticamente a quem publicou.
2. **Proteção das tags de release (recomendado).** Crie um ruleset de tags (**Settings → Rules →
   Rulesets → New tag ruleset**) para `refs/tags/v*`, bloqueando atualização e exclusão (e, se quiser,
   restringindo a criação a administradores). Isso impede mover ou apagar a tag de uma versão
   publicada.
3. **Releases imutáveis (recomendado, se disponível).** Em **Settings → General → Releases**, ative a
   imutabilidade de releases, para que tag e assets de uma release publicada não possam ser alterados.
4. Nenhum secret precisa ser criado: o workflow usa só o `GITHUB_TOKEN`.

A primeira release (`v0.1.0` ou outra) é uma ação consciente do mantenedor, feita com os comandos de
[Criar uma release](#criar-uma-release). A implementação desta issue não criou nenhuma tag nem release.

## Política verificada automaticamente

`scripts/tests/workflows.test.sh` (check `deploy-scripts`) reprova um PR que altere `release.yml` para:

- disparar por PR, `workflow_dispatch`, branch ou agendamento, em vez de só `push` de tag `v*.*.*`;
- aceitar tags fora de `vMAJOR.MINOR.PATCH` (a expressão é testada com exemplos válidos e inválidos);
- dar escrita no topo do workflow, dar escrita além de `packages: write` no `publish` e de
  `contents: write` no `github-release`, ou fazer checkout ou build nesses jobs;
- usar runner que não seja `ubuntu-latest`, omitir `timeout-minutes` ou cancelar uma release em andamento;
- remover a verificação de `main`, a resolução `^{commit}`, o gate do Trivy, a SBOM, a conferência do ID
  da imagem, a recusa de versão repetida, a tag SemVer, a tag do commit ou o digest;
- publicar `latest`, usar secrets, passar o token por argumento, usar `eval`/`source`, persistir
  credenciais do checkout ou usar `${{ }}` dentro de scripts;
- usar um Trivy diferente do `container-security.yml`.

`scripts/tests/compose-release.test.sh` garante que o overlay usa a imagem da release sem build local,
exige `ALMIRANTE_API_IMAGE` e não muda nada quando não é usado.
