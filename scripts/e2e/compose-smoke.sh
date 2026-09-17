#!/usr/bin/env bash
# Smoke test da stack Docker Compose já em execução (docker compose up -d --build).
# Valida o que só existe na topologia real: TLS no nginx, redirecionamento HTTP, key ring do Data
# Protection no volume, confiança restrita ao nginx, rate limit e conexões persistentes sob carga.
#
# Uso (na raiz do repositório):
#   scripts/e2e/compose-smoke.sh                      # usa ./.env
#   ENV_FILE=outro.env COMPOSE_PROJECT=x scripts/e2e/compose-smoke.sh
#
# Requer: bash, curl, docker. Usa -k no curl porque o certificado de desenvolvimento pode não ser
# confiável dentro do host Docker; defina CURL_CA=/caminho/ca.pem para validar a cadeia.
set -euo pipefail

ENV_FILE="${ENV_FILE:-.env}"
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

compose=(docker compose --env-file "$ENV_FILE")
[ -n "${COMPOSE_PROJECT:-}" ] && compose+=(-p "$COMPOSE_PROJECT")
HTTPS="https://localhost:${API_HTTPS_HOST_PORT:-8443}"
HTTP="http://localhost:${API_HOST_PORT:-8090}"
curl_tls=(curl -sS --max-time 30)
if [ -n "${CURL_CA:-}" ]; then curl_tls+=(--cacert "$CURL_CA"); else curl_tls+=(-k); fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
failures=0
pass() { printf 'PASS %s\n' "$1"; }
fail() { printf 'FAIL %s :: %s\n' "$1" "$2"; failures=$((failures + 1)); }
check() { if [ "$2" = "$3" ]; then pass "$1"; else fail "$1" "esperado=$3 obtido=$2"; fi; }

api="$("${compose[@]}" ps -q api)"
nginx="$("${compose[@]}" ps -q nginx)"
[ -n "$api" ] && [ -n "$nginx" ] || { echo "stack não está em execução (api/nginx)"; exit 2; }

for _ in $(seq 1 30); do
  [ "$(docker inspect -f '{{.State.Health.Status}}' "$nginx")" = healthy ] && break
  sleep 2
done
check "nginx healthy (healthcheck em IPv4)" "$(docker inspect -f '{{.State.Health.Status}}' "$nginx")" healthy

check "HTTP /health responde 200" "$(curl -sS -o /dev/null -w '%{http_code}' "$HTTP/health")" 200
redirect="$(curl -sS -o /dev/null -w '%{http_code} %{redirect_url}' "$HTTP/api/Auth/csrf")"
check "HTTP redireciona a API para HTTPS" "$redirect" "308 https://localhost:${API_HTTPS_HOST_PORT:-8443}/api/Auth/csrf"

# CSRF via TLS: extrai o token do corpo e o cookie do header (sem jq).
"${curl_tls[@]}" -D "$tmp/csrf.h" -o "$tmp/csrf.b" "$HTTPS/api/Auth/csrf"
check "HTTPS GET csrf responde 200" "$(head -1 "$tmp/csrf.h" | awk '{print $2}')" 200
csrf="$(sed -E 's/.*"csrfToken":"([^"]+)".*/\1/' "$tmp/csrf.b")"
csrf_cookie="$(grep -i '^set-cookie: __Host-almirante-csrf=' "$tmp/csrf.h" | sed -E 's/^[^:]+: ([^;]+);.*/\1/' | tr -d '\r')"
grep -qi '^strict-transport-security:' "$tmp/csrf.h" && pass "HSTS presente na resposta HTTPS" || fail "HSTS presente na resposta HTTPS" "header ausente"

owner="$(docker exec "$api" sh -c 'stat -c %U /var/lib/almirante/dataprotection && ls /var/lib/almirante/dataprotection | grep -c "\.xml$"' | tr '\n' ' ')"
case "$owner" in app\ [1-9]*) pass "key ring do Data Protection gravado no volume pelo usuário app ($owner)";; *) fail "key ring do Data Protection" "$owner";; esac

post() { # post <path> <cookie> <csrf> [json] [bearer] -> escreve $tmp/r.h e $tmp/r.b
  local args=(-D "$tmp/r.h" -o "$tmp/r.b" -X POST -H "Cookie: $2" -H "X-CSRF-TOKEN: $3")
  [ -n "${4:-}" ] && args+=(-H 'Content-Type: application/json' --data "$4")
  [ -n "${5:-}" ] && args+=(-H "Authorization: Bearer $5")
  "${curl_tls[@]}" "${args[@]}" "$HTTPS$1"
}
status() { head -1 "$tmp/r.h" | awk '{print $2}'; }
refresh_cookie() { grep -i '^set-cookie: __Host-almirante-refresh=' "$tmp/r.h" | sed -E 's/^[^:]+: ([^;]+);.*/\1/' | tr -d '\r'; }

post /api/Auth/login "$csrf_cookie" "$csrf" "{\"email\":\"${SEED_ADMIN_EMAIL}\",\"senha\":\"${SEED_ADMIN_SENHA}\"}"
check "login via TLS responde 200" "$(status)" 200
grep -q '^{"token":{"accessToken":"[^"]*","expiresAtUtc":"[^"]*"}}$' "$tmp/r.b" && pass "login devolve só o envelope token" || fail "login devolve só o envelope token" "$(head -c 120 "$tmp/r.b")"
access="$(sed -E 's/.*"accessToken":"([^"]+)".*/\1/' "$tmp/r.b")"
refresh="$(refresh_cookie)"
grep -i '^set-cookie: __Host-almirante-refresh=' "$tmp/r.h" | grep -qi 'secure' && pass "cookie de refresh Secure" || fail "cookie de refresh Secure" "atributo ausente"

check "/Me com bearer responde 200" "$("${curl_tls[@]}" -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $access" "$HTTPS/api/Auth/Me")" 200
post /api/Auth/refresh "$csrf_cookie; $refresh" "$csrf"
check "refresh só com cookie + CSRF responde 200" "$(status)" 200
refresh="$(refresh_cookie)"
post /api/Auth/logout "$csrf_cookie; $refresh" "$csrf" "" "$access"
check "logout responde 204" "$(status)" 204
post /api/Auth/refresh "$csrf_cookie; $refresh" "$csrf"
check "refresh após logout responde 401" "$(status)" 401

direct="$(docker exec "$api" curl -s -o /dev/null -w '%{http_code}' -H 'X-Forwarded-Proto: https' -H 'X-Forwarded-For: 203.0.113.77' http://127.0.0.1:8080/api/Auth/csrf)"
check "API ignora X-Forwarded-* de origem que não é o nginx (HTTPS obrigatório)" "$direct" 400

echo "aguardando a janela do rate limit do login (61s)..."
sleep 61
preflights=""
for _ in 1 2 3 4 5 6; do
  preflights+="$("${curl_tls[@]}" -o /dev/null -w '%{http_code} ' -X OPTIONS -H 'Origin: https://localhost:4200' -H 'Access-Control-Request-Method: POST' "$HTTPS/api/Auth/login")"
done
case "$preflights" in *429*) fail "preflight OPTIONS não consome o rate limit do login" "$preflights";; *) pass "preflight OPTIONS não consome o rate limit do login ($preflights)";; esac
codes=""
for _ in 1 2 3 4; do
  post /api/Auth/login "$csrf_cookie" "$csrf" '{"email":"ninguem@local.dev","senha":"errada"}'
  codes+="$(status) "
done
check "rate limit do login: 3 tentativas e depois 429" "$codes" "401 401 401 429 "
grep -qi '^retry-after: 60' "$tmp/r.h" && grep -qi '^content-type: application/problem+json' "$tmp/r.h" \
  && pass "429 do login com Retry-After e Problem Details" || fail "429 do login com Retry-After e Problem Details" "$(tr -d '\r' < "$tmp/r.h" | tr '\n' '|')"

since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
load="$(seq 3000 | xargs -P 32 -I{} "${curl_tls[@]}" -o /dev/null -w '%{http_code}\n' "$HTTPS/api/Auth/csrf" | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
crit="$(docker logs --since "$since" "$nginx" 2>&1 | grep -c '\[crit\]' || true)"
case "$load" in " 3000 200;") check "3000 requisições concorrentes sem erro de upstream ([crit]=0)" "$crit" 0;; *) fail "3000 requisições concorrentes" "$load crit=$crit";; esac

if [ "$failures" -gt 0 ]; then
  echo "FALHAS: $failures"
  exit 1
fi
echo "Smoke test da stack concluído sem falhas."
