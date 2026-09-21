#!/usr/bin/env bash
# Testa, sobre a configuração EFETIVA do Compose (docker compose config, com valores sentinela e sem subir nada),
# a separação de identidades SQL: a API nunca recebe o segredo do "sa", não existe fallback para "sa", o
# healthcheck do SQL Server não autentica como "sa" e o bootstrap roda antes da API. Precisa de "docker compose".
# Uso: bash scripts/tests/compose-sql-identities.test.sh
set -uo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
falhas=0

ok() { echo "ok   - $1"; }
falha() { falhas=$((falhas + 1)); echo "FAIL - $1 :: $2"; }

docker compose version > /dev/null 2>&1 || { echo "docker compose indisponível: teste não pode rodar." >&2; exit 2; }

SA_SENTINEL='SENTINELA_SA_9f3c1a77'
ADMIN_SENTINEL='SENTINELA_ADMIN_4b2e8d01'

base_env() {
  cat <<EOF
SQL_SA_PASSWORD=$SA_SENTINEL
SEED_ADMIN_SENHA=Senha-Seed-Para-Teste-2026
JWT_KEY_V1=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
TLS_CERT_PATH=$TMP/cert.pem
TLS_KEY_PATH=$TMP/key.pem
TLS_PUBLIC_HOST=api.exemplo.test
EOF
}
completo() { base_env; printf 'SQL_ADMIN_USER=almirante_admin_bd\nSQL_ADMIN_PASSWORD=%s\n' "$ADMIN_SENTINEL"; }

# compose_config ARQUIVO_ENV [-f arquivo...] -> configuração normalizada em stdout; status do compose
compose_config() {
  local envfile=$1; shift
  local files=("$@"); [[ ${#files[@]} -gt 0 ]] || files=(-f compose.yaml)
  (cd "$ROOT" && env -u SQL_ADMIN_USER -u SQL_ADMIN_PASSWORD -u SQL_SA_PASSWORD -u SQL_APP_USER docker compose --env-file "$envfile" "${files[@]}" config 2>&1; exit "${PIPESTATUS[0]}") | desdobra
  return "${PIPESTATUS[0]}"
}

# O compose quebra valores longos em várias linhas; junta as continuações para os greps abaixo.
desdobra() {
  awk '{
    if (prev != "" && $0 ~ /^ +/ && $0 !~ /^ *- / && $0 !~ /^ *[A-Za-z0-9_."\/-]+:( |$)/) { sub(/^ +/, ""); prev = prev " " $0 }
    else { if (prev != "") print prev; prev = $0 }
  } END { print prev }'
}

# bloco_servico SERVICO < config -> trecho do serviço (indentação de 2 espaços sob "services:")
bloco_servico() {
  awk -v alvo="  $1:" '
    /^services:/ { dentro_servicos = 1; next }
    /^[^[:space:]]/ { dentro_servicos = 0 }
    dentro_servicos && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { atual = ($0 == alvo); next }
    dentro_servicos && atual { print }
  '
}

# 1. Variáveis administrativas obrigatórias: sem elas o Compose recusa (não existe valor padrão / fallback).
base_env > "$TMP/sem-admin.env"
saida=$(compose_config "$TMP/sem-admin.env")
if [[ $? -ne 0 && "$saida" == *"SQL_ADMIN_USER"* ]]; then ok "sem SQL_ADMIN_USER o Compose recusa (nenhum fallback para sa)"; else falha "SQL_ADMIN_USER obrigatório" "$saida"; fi

{ base_env; echo 'SQL_ADMIN_USER=almirante_admin_bd'; } > "$TMP/sem-senha.env"
saida=$(compose_config "$TMP/sem-senha.env")
if [[ $? -ne 0 && "$saida" == *"SQL_ADMIN_PASSWORD"* ]]; then ok "sem SQL_ADMIN_PASSWORD o Compose recusa (nenhum fallback para a senha do sa)"; else falha "SQL_ADMIN_PASSWORD obrigatória" "$saida"; fi

# 2. Nos arquivos do Compose (sem contar comentários), nenhum "-sa" como valor padrão nem "User Id=sa".
for arquivo in compose.yaml compose.tls.yaml compose.https.yaml; do
  codigo=$(grep -vE '^[[:space:]]*#' "$ROOT/$arquivo")
  if grep -qiE 'SQL_ADMIN_(USER|PASSWORD):-|:-sa\b|User Id=sa\b|SQL_ADMIN_PRIVILEGE_CHECK|AdminPrivilegeCheck' <<<"$codigo"; then
    falha "$arquivo" "contém fallback para sa ou modo permissivo de auditoria"
  else ok "$arquivo: sem fallback para sa nem modo permissivo"; fi
done

# 3. Configuração efetiva para cada combinação de overlays usada em produção/dev.
completo > "$TMP/completo.env"
combos=("-f compose.yaml" "-f compose.yaml -f compose.tls.yaml" "-f compose.yaml -f compose.https.yaml")
for combo in "${combos[@]}"; do
  # shellcheck disable=SC2086
  config=$(compose_config "$TMP/completo.env" $combo)
  if [[ $? -ne 0 ]]; then falha "config ($combo)" "$config"; continue; fi

  api=$(bloco_servico api <<<"$config")
  [[ -n "$api" ]] || { falha "config ($combo)" "serviço api não encontrado"; continue; }

  if grep -qF "$SA_SENTINEL" <<<"$api" || grep -qiE 'MSSQL_SA_PASSWORD|SQL_SA_PASSWORD|SQLCMDPASSWORD' <<<"$api"; then
    falha "API não recebe o segredo do sa ($combo)" "encontrado no bloco da api"
  else ok "API não recebe o segredo do sa ($combo)"; fi

  if grep -qiE 'User Id=sa(;|$)' <<<"$api"; then falha "API sem usuário sa ($combo)" "connection string com sa"; else ok "API sem connection string com sa ($combo)"; fi

  if grep -q "User Id=almirante_admin_bd;Password=$ADMIN_SENTINEL" <<<"$api"; then ok "API usa a identidade administrativa dedicada ($combo)"; else falha "identidade administrativa ($combo)" "connection string administrativa ausente"; fi
  if grep -q 'DbCredentials__AppUser: almirante_user_bd' <<<"$api"; then ok "API usa a identidade de runtime ($combo)"; else falha "identidade de runtime ($combo)" "DbCredentials__AppUser ausente"; fi
  if grep -q 'ConnectionStrings__almirante: Server=sqlserver,1433;Database=almirante;Encrypt=True' <<<"$api" && ! grep -q 'ConnectionStrings__almirante:.*Password' <<<"$api"; then
    ok "connection string de runtime sem credencial ($combo)"; else falha "connection string de runtime ($combo)" "contém credencial ou mudou"; fi
  if grep -q 'sql-bootstrap:' <<<"$api" && grep -q 'condition: service_completed_successfully' <<<"$api"; then ok "API só sobe depois do sql-bootstrap concluído ($combo)"; else falha "depends_on ($combo)" "api não espera o sql-bootstrap"; fi
  if grep -q 'env_file' <<<"$api"; then falha "API sem env_file ($combo)" "env_file repassaria o .env inteiro"; else ok "API sem env_file ($combo)"; fi

  # O segredo do sa só aparece no sqlserver (MSSQL_SA_PASSWORD) e no sql-bootstrap (SQLCMDPASSWORD).
  donos=$(awk -v s="$SA_SENTINEL" '
    /^services:/ { d = 1; next } /^[^[:space:]]/ { d = 0 }
    d && /^  [A-Za-z0-9_-]+:[[:space:]]*$/ { srv = $1; sub(/:/, "", srv); next }
    d && index($0, s) { print srv }' <<<"$config" | sort -u | tr '\n' ' ')
  if [[ "$donos" == "sql-bootstrap sqlserver " ]]; then ok "o segredo do sa só existe em sqlserver e sql-bootstrap ($combo)"; else falha "donos do segredo do sa ($combo)" "encontrado em: $donos"; fi
done

# 4. SQL Server: healthcheck sem "sa"/senha; bootstrap uma vez (sem restart), sem publicar porta.
config=$(compose_config "$TMP/completo.env")
sqlserver=$(bloco_servico sqlserver <<<"$config")
health=$(awk '/healthcheck:/ { d = 1 } d { print }' <<<"$sqlserver")
if [[ -n "$health" ]] && ! grep -qE -- '-U sa|SA_PASSWORD|SQLCMDPASSWORD' <<<"$health" && grep -q 'Login failed for user' <<<"$health"; then
  ok "healthcheck do SQL Server não autentica como sa (readiness pela resposta a um login inexistente)"
else falha "healthcheck do sqlserver" "$health"; fi

bootstrap=$(bloco_servico sql-bootstrap <<<"$config")
if grep -qE "restart: .no.$" <<<"$bootstrap" && ! grep -q 'published:' <<<"$bootstrap"; then ok "sql-bootstrap roda uma vez, sem restart e sem porta publicada"; else falha "sql-bootstrap" "$bootstrap"; fi
if grep -qE 'SQLCMDPASSWORD: ' <<<"$bootstrap" && grep -q 'ADMIN_USER: almirante_admin_bd' <<<"$bootstrap"; then ok "sql-bootstrap recebe o sa por variável de ambiente e a identidade administrativa"; else falha "sql-bootstrap env" "$bootstrap"; fi
if grep -qE 'command:|entrypoint:.*(-P|-p )' <<<"$bootstrap" && grep -qF "$SA_SENTINEL" <<<"$(grep -E 'command:|entrypoint:' <<<"$bootstrap")"; then falha "sql-bootstrap argv" "senha na linha de comando"; else ok "sql-bootstrap sem senha na linha de comando"; fi

echo
echo "$falhas falha(s)."
((falhas == 0))
