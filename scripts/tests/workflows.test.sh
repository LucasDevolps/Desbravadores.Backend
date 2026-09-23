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
# Sem exceções para build/testes: compilar uma PR também executa código vindo dela.
set -uo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
WF="$REPO/.github/workflows"
falhas=0

erro() { echo "FALHOU: $*" >&2; falhas=$((falhas + 1)); }
ok() { echo "ok: $*"; }

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
      erro "$nome/$job: job disparado por pull_request usa runner self-hosted ($(tr -s ' ' <<<"$runs" | sed 's/^ //')). Código de PR não confiável não pode rodar na máquina de deploy — use runner hospedado ou mova o job para um workflow 'on: push'."
    else
      ok "$nome/$job: runner hospedado"
    fi
  done < <(jobs_de "$arquivo")
done

# 4. backend-ci.yml: cobertura das duas suítes, relatório, resumo e quality gate (#67), sem perder a integração
#    SQL Server real. Verifica comandos/propriedades dentro de cada job, não posições no YAML.
CI="$WF/backend-ci.yml"
exige() { # JOB TRECHO DESCRICAO
  if bloco_do_job "$CI" "$1" | grep -qF -- "$2"; then
    ok "backend-ci.yml/$1: $3"
  else
    erro "backend-ci.yml/$1: $3 (não encontrado: '$2')."
  fi
}
if [[ -f "$CI" ]]; then
  for job in build-and-test sqlserver-integration; do
    exige "$job" '--collect "XPlat Code Coverage"' "coleta cobertura"
    exige "$job" '--settings backend/coverage.runsettings' "usa backend/coverage.runsettings"
    exige "$job" 'path: TestResults/*/coverage.cobertura.xml' "publica o Cobertura bruto"
  done
  exige build-and-test 'Category!=RequiresDocker&Category!=RequiresSqlServer' "roda os testes sem dependências externas"
  exige sqlserver-integration 'Category=RequiresSqlServer' "roda os testes RequiresSqlServer"
  exige sqlserver-integration 'mcr.microsoft.com/mssql/server:2022' "usa SQL Server 2022 descartável"
  exige sqlserver-integration 'openssl rand' "gera senha aleatória por execução"
  exige sqlserver-integration '--publish 127.0.0.1::1433' "expõe o SQL só em loopback"
  if bloco_do_job "$CI" sqlserver-integration | grep -B2 -F 'docker rm --force almirante-ci-sql' | grep -qF 'if: always()'; then
    ok "backend-ci.yml/sqlserver-integration: remove o container mesmo em falha"
  else
    erro "backend-ci.yml/sqlserver-integration: a remoção do SQL descartável precisa de 'if: always()'."
  fi
  exige coverage 'needs: [build-and-test, sqlserver-integration]' "consolida as duas suítes"
  exige coverage 'dotnet tool restore' "usa o ReportGenerator fixado no manifesto local"
  exige coverage 'bash scripts/coverage.sh report' "gera o relatório consolidado"
  exige coverage 'bash scripts/coverage.sh gate' "aplica o quality gate"
  exige coverage 'backend/coverage-baseline.json' "compara com o baseline versionado"
  exige coverage 'GITHUB_STEP_SUMMARY' "escreve o Job Summary"
  exige coverage 'name: coverage-${{' "publica o artifact de cobertura"
  exige deploy-scripts 'bash scripts/tests/coverage.test.sh' "testa o script de cobertura"
  if grep -qE '^[[:space:]]*continue-on-error:' "$CI"; then
    erro "backend-ci.yml: 'continue-on-error' esconderia falha de teste ou do quality gate."
  else
    ok "backend-ci.yml: nenhum passo com continue-on-error"
  fi
else
  erro "backend-ci.yml não encontrado."
fi

if ((falhas > 0)); then
  echo
  echo "$falhas verificação(ões) de política falharam." >&2
  exit 1
fi
echo
echo "Política dos workflows: tudo certo."
