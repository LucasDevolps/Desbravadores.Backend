#!/usr/bin/env bash
# Política dos workflows do GitHub Actions. Bash puro (sem yq/python) para rodar em qualquer runner.
#
# Regra central: um job disparado por `pull_request` executa código que vem do HEAD da PR. Como este
# repositório é PÚBLICO e aceita fork, esse código é NÃO CONFIÁVEL. O único runner self-hosted Linux
# registrado é a máquina de deploy (guarda ~/almirante/.env com senha do sa, chave JWT e senha do
# admin, além de acesso ao daemon Docker), e o Windows publica o site do IIS. Portanto:
#
#   nenhum job de um workflow com gatilho `pull_request` pode usar runner self-hosted.
#
# O job `build-and-test` é a exceção conhecida e explicitamente registrada abaixo (compila e roda a
# suíte .NET, o que já é execução de código da PR); ela existe porque aquele runner não guarda o
# .env de deploy. Ao remover essa exceção, este teste passa a exigir runner hospedado também nele.
set -uo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
WF="$REPO/.github/workflows"
falhas=0

erro() { echo "FALHOU: $*" >&2; falhas=$((falhas + 1)); }
ok() { echo "ok: $*"; }

# Jobs autorizados a rodar em self-hosted mesmo com gatilho pull_request (arquivo:job).
EXCECOES=("backend-ci.yml:build-and-test")

e_excecao() {
  local alvo=$1 item
  for item in "${EXCECOES[@]}"; do [[ "$item" == "$alvo" ]] && return 0; done
  return 1
}

# Lista os nomes de job (chaves com 2 espaços de indentação sob "jobs:") de um workflow.
jobs_de() {
  awk '
    /^jobs:[[:space:]]*$/ { dentro = 1; next }
    /^[^[:space:]#]/      { dentro = 0 }
    dentro && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ {
      linha = $0; sub(/^  /, "", linha); sub(/:[[:space:]]*$/, "", linha); print linha
    }
  ' "$1"
}

# Bloco de texto de um job (da sua linha até o próximo job ou o fim do arquivo).
bloco_do_job() {
  awk -v alvo="  $2:" '
    index($0, alvo) == 1 { dentro = 1; print; next }
    dentro && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { dentro = 0 }
    dentro { print }
  ' "$1"
}

# Cabeçalho do workflow: tudo antes de "jobs:".
cabecalho() { awk '/^jobs:[[:space:]]*$/ { exit } { print }' "$1"; }

for arquivo in "$WF"/*.yml "$WF"/*.yaml; do
  [[ -e "$arquivo" ]] || continue
  nome=$(basename "$arquivo")
  head=$(cabecalho "$arquivo")

  # 1. permissions explícito no topo (menor privilégio para o GITHUB_TOKEN).
  if grep -qE '^permissions:' <<<"$head"; then
    ok "$nome: declara permissions no topo"
  else
    erro "$nome: falta 'permissions:' no topo do workflow (use ao menos 'contents: read')."
  fi

  # 2. actions de terceiros pinadas por SHA de 40 hex, não por tag mutável.
  while IFS= read -r linha; do
    ref=${linha#*uses:}
    ref=$(tr -d ' \r' <<<"$ref")
    [[ "$ref" == ./* ]] && continue # action local do próprio repo
    if [[ ! "$ref" =~ @[0-9a-f]{40}$ ]]; then
      erro "$nome: action '$ref' não está pinada por SHA de 40 caracteres."
    fi
  done < <(grep -E '^[[:space:]]*-?[[:space:]]*uses:' "$arquivo" | sed 's/#.*//')

  # 3. o cerne: pull_request + self-hosted.
  grep -qE '^[[:space:]]{2}pull_request:' <<<"$head" || { ok "$nome: sem gatilho pull_request"; continue; }

  while IFS= read -r job; do
    [[ -n "$job" ]] || continue
    runs=$(bloco_do_job "$arquivo" "$job" | grep -E '^[[:space:]]*runs-on:' | head -1)
    if grep -q 'self-hosted' <<<"$runs"; then
      if e_excecao "$nome:$job"; then
        ok "$nome/$job: self-hosted com pull_request (exceção registrada)"
      else
        erro "$nome/$job: job disparado por pull_request usa runner self-hosted ($(tr -s ' ' <<<"$runs" | sed 's/^ //')). Código de PR não confiável não pode rodar na máquina de deploy — use 'runs-on: ubuntu-latest' ou mova o job para um workflow 'on: push'."
      fi
    else
      ok "$nome/$job: runner hospedado"
    fi
  done < <(jobs_de "$arquivo")
done

if ((falhas > 0)); then
  echo
  echo "$falhas verificação(ões) de política falharam." >&2
  exit 1
fi
echo
echo "Política dos workflows: tudo certo."
