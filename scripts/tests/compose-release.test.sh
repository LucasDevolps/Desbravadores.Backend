#!/usr/bin/env bash
# Testa o overlay compose.release.yaml (issue #70) sobre a configuração EFETIVA do Compose (docker compose config,
# sem subir nada): a API passa a vir da imagem publicada da release, sem "build:", com a referência obrigatória;
# e sem o overlay nada muda (desenvolvimento local e deploy de develop continuam construindo a imagem).
# Precisa de "docker compose". Uso: bash scripts/tests/compose-release.test.sh
set -uo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
falhas=0

ok() { echo "ok   - $1"; }
falha() { falhas=$((falhas + 1)); echo "FAIL - $1 :: $2"; }

docker compose version > /dev/null 2>&1 || { echo "docker compose indisponível: teste não pode rodar." >&2; exit 2; }

IMAGEM_DIGEST="ghcr.io/lucasdevolps/almirante-api@sha256:$(printf '%064d' 0)"

cat > "$TMP/base.env" <<EOF
SQL_SA_PASSWORD=Sentinela-Sa-Para-Teste-2026
SEED_ADMIN_SENHA=Senha-Seed-Para-Teste-2026
JWT_KEY_V1=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
SQL_ADMIN_USER=almirante_admin_bd
SQL_ADMIN_PASSWORD=Sentinela-Admin-Para-Teste-2026
TLS_CERT_PATH=$TMP/cert.pem
TLS_KEY_PATH=$TMP/key.pem
TLS_PUBLIC_HOST=api.exemplo.test
EOF
{ cat "$TMP/base.env"; echo "ALMIRANTE_API_IMAGE=$IMAGEM_DIGEST"; } > "$TMP/release.env"

# compose_config ARQUIVO_ENV arquivos... -> configuração normalizada em stdout; status do compose
compose_config() {
  local envfile=$1; shift
  (cd "$ROOT" && env -u ALMIRANTE_API_IMAGE docker compose --env-file "$envfile" "$@" config 2>&1)
}

# bloco da API (indentação de 2 espaços sob "services:")
bloco_api() {
  awk '
    /^services:/ { d = 1; next } /^[^[:space:]]/ { d = 0 }
    d && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { atual = ($0 == "  api:"); next }
    d && atual { print }'
}

# 1. Sem o overlay: a API continua sendo construída do Dockerfile (dev local e deploy de develop intactos).
config=$(compose_config "$TMP/release.env" -f compose.yaml)
api=$(bloco_api <<<"$config")
if grep -qE '^    build:' <<<"$api" && grep -q 'dockerfile: Almirante.Api/Dockerfile' <<<"$api" && ! grep -q 'ghcr.io' <<<"$api"; then
  ok "sem o overlay a API continua com build local (compose.yaml inalterado)"
else falha "compose.yaml sem overlay" "$api"; fi

# 2. Referência obrigatória: sem ALMIRANTE_API_IMAGE o Compose recusa (nenhum fallback para latest/build).
saida=$(compose_config "$TMP/base.env" -f compose.yaml -f compose.release.yaml)
if [[ $? -ne 0 && "$saida" == *"ALMIRANTE_API_IMAGE"* ]]; then ok "sem ALMIRANTE_API_IMAGE o overlay recusa"; else falha "ALMIRANTE_API_IMAGE obrigatória" "$saida"; fi

# 3. Com o overlay (sozinho e com TLS): imagem da release, sem build, sem pull de tag móvel.
for combo in "-f compose.yaml -f compose.release.yaml" "-f compose.yaml -f compose.tls.yaml -f compose.release.yaml"; do
  # shellcheck disable=SC2086
  config=$(compose_config "$TMP/release.env" $combo)
  if [[ $? -ne 0 ]]; then falha "config ($combo)" "$config"; continue; fi
  api=$(bloco_api <<<"$config")
  if grep -qF "image: $IMAGEM_DIGEST" <<<"$api"; then ok "API usa a imagem da release ($combo)"; else falha "imagem ($combo)" "$api"; fi
  if grep -qE '^    build:' <<<"$api"; then falha "build removido ($combo)" "o overlay ainda herda build: do compose.yaml"; else ok "API sem build local ($combo)"; fi
  if grep -q 'pull_policy: missing' <<<"$api"; then ok "pull_policy missing ($combo)"; else falha "pull_policy ($combo)" "$api"; fi
  # O resto da API (healthcheck, volume do Data Protection, hardening) continua vindo do compose.yaml.
  if grep -q 'http://localhost:8080/health' <<<"$api" && grep -q 'target: /var/lib/almirante/dataprotection' <<<"$api" && grep -q 'no-new-privileges:true' <<<"$api"; then
    ok "healthcheck, volume e hardening preservados ($combo)"
  else falha "configuração herdada ($combo)" "$api"; fi
done

# 4. O overlay não fixa tag móvel nem imagem padrão.
codigo=$(grep -vE '^[[:space:]]*#' "$ROOT/compose.release.yaml")
if grep -qE ':latest|ALMIRANTE_API_IMAGE:-' <<<"$codigo"; then falha "compose.release.yaml" "usa latest ou valor padrão para a imagem"; else ok "compose.release.yaml sem latest nem imagem padrão"; fi

echo
echo "$falhas falha(s)."
((falhas == 0))
