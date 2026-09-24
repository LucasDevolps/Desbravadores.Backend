# ADR-0010: Publicar releases como imagem imutável no GHCR a partir de tags SemVer

## Status

Accepted. Implementado na issue #70.

## Contexto

Até a issue #70 o projeto não tinha versões numeradas ([`SECURITY.md`](../../../SECURITY.md) dizia isso
explicitamente). O deploy Linux constrói a imagem da API na própria máquina a cada push em `develop`
(`docker compose ... up -d --build` em
[`backend-deploy.yml`](../../../.github/workflows/backend-deploy.yml)), e o deploy Windows faz
`dotnet publish` no host. Não havia artefato que pudesse ser identificado depois, promovido entre
ambientes ou usado em rollback. O `container-security.yml` já construía e escaneava a imagem, mas
descartava o resultado.

Forças em tensão:

- rastreabilidade: tag, commit, release e imagem precisam apontar uns para os outros;
- imutabilidade: uma versão não pode mudar de conteúdo, e `--pull` faz dois builds do mesmo commit
  gerarem imagens diferentes;
- menor privilégio: o repositório é público, e os runners self-hosted guardam o `.env` de produção
  ([`scripts/tests/workflows.test.sh`](../../../scripts/tests/workflows.test.sh));
- não quebrar o deploy contínuo de `develop` nem o desenvolvimento local.

## Decisão

- Versões seguem SemVer, como tags Git `vMAJOR.MINOR.PATCH` (sem pré-release) criadas a partir de `main`.
- O workflow [`release.yml`](../../../.github/workflows/release.yml) roda só em push dessas tags, em
  `ubuntu-latest`, e segue esta sequência: valida a tag e o vínculo com `main`, constrói **uma** imagem
  do commit da tag, aplica o gate do Trivy, gera a SBOM, publica **a mesma imagem** (conferida pelo ID
  após `docker save`/`docker load`) e, por último, cria a GitHub Release.
- A imagem é `ghcr.io/<owner em minúsculas>/almirante-api`, com as tags `vX.Y.Z` e `sha-<commit completo>`
  e o digest registrado na release. Não há `latest`.
- Escrita no `GITHUB_TOKEN` só por job: `packages: write` no `publish` e `contents: write` no
  `github-release`. Nenhum desses jobs faz checkout ou build.
- Versões publicadas não são sobrescritas: release ou tag de imagem já existente reprova o workflow.
- Ambientes com Compose consomem a release pelo overlay
  [`compose.release.yaml`](../../../compose.release.yaml), de preferência por digest. O rollback é trocar
  o digest.

Fora do escopo: o deploy contínuo de `develop` continua construindo localmente, o deploy Windows/IIS
continua com `dotnet publish`, rollback de banco não é automatizado, e attestations/provenance (SLSA)
não são geradas.

## Consequências

### Positivas

- Cada versão tem uma cadeia verificável: tag → commit → GitHub Release → imagem → digest.
- O artefato escaneado é o artefato publicado. Rollback e promoção usam uma imagem já validada, sem
  rebuild.
- Nenhum secret novo: o GHCR usa o `GITHUB_TOKEN`, e o token de escrita nunca executa código do
  repositório.

### Negativas / trade-offs

- Dois fluxos coexistem: `develop` (build local, sem versão) e releases (imagem do GHCR). Um host que
  roda uma release não pode receber também o deploy contínuo de `develop`.
- A imagem passa por um artifact do run (`docker save`, retenção de 1 dia) entre build e publicação.
- Duas versões no mesmo commit não são possíveis (a tag `sha-<commit>` já existiria).
- Visibilidade do package, ruleset de tags e releases imutáveis são configuração manual no GitHub
  ([`docs/release-process.md`](../../release-process.md#configuração-manual-no-github)).
- Voltar a imagem não reverte migrations
  ([ADR-0009](0009-aplicar-migrations-na-inicializacao-da-api.md)). A compatibilidade do schema é
  verificada pelo operador.

## Alternativas consideradas

- **Publicar a partir do `container-security.yml`:** esse workflow roda em PRs de fork. Dar
  `packages: write` a ele misturaria código não confiável com escrita no registry.
- **Reconstruir a imagem no job de publicação:** com `--pull` a imagem publicada poderia ser diferente
  da escaneada. Foi descartado.
- **Publicar `latest`:** tag móvel não serve para rollback nem prova o que roda. Foi descartado.
- **Trocar o deploy de `develop` para puxar do GHCR:** commits de `develop` não têm versão, e a troca
  mudaria o fluxo de integração sem necessidade. Foi adiado.

## Evidências no repositório

- Workflow: [`release.yml`](../../../.github/workflows/release.yml), jobs `validate`, `build-scan`,
  `publish` e `github-release`.
- Política: seção 8 de [`workflows.test.sh`](../../../scripts/tests/workflows.test.sh).
- Overlay: [`compose.release.yaml`](../../../compose.release.yaml) e
  [`compose-release.test.sh`](../../../scripts/tests/compose-release.test.sh).
- Processo, rollback e migrations: [`docs/release-process.md`](../../release-process.md).

## Decisões relacionadas

- [ADR-0009](0009-aplicar-migrations-na-inicializacao-da-api.md): migrations na inicialização da API.
- [ADR-0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md): identidades SQL, preservadas pelo
  overlay de release.
